using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

[CanEditMultipleObjects]
[CustomEditor(typeof(CloudLayer))]
internal sealed class CloudLayerEditor : VolumeComponentEditor
{
    private struct CloudMapParameters
    {
        internal SerializedDataParameter cloudMap;
        internal SerializedDataParameter[] opacities;
        internal SerializedDataParameter altitude;
        internal SerializedDataParameter rotation;
        internal SerializedDataParameter tint;
        internal SerializedDataParameter exposure;
        internal SerializedDataParameter distortion;
        internal SerializedDataParameter scrollOrientation;
        internal SerializedDataParameter scrollSpeed;
        internal SerializedDataParameter flowmap;
        internal SerializedDataParameter raymarching;
        internal SerializedDataParameter steps;
        internal SerializedDataParameter thickness;
        internal SerializedDataParameter probeDimmer;
        internal SerializedDataParameter castShadows;
    }

    private SerializedDataParameter _state;
    private SerializedDataParameter _opacity;
    private SerializedDataParameter _upperHemisphereOnly;
    private SerializedDataParameter _layerCount;
    private SerializedDataParameter _resolution;
    private SerializedDataParameter _planetRadius;
    private SerializedDataParameter _shadowMultiplier;
    private SerializedDataParameter _shadowTint;
    private SerializedDataParameter _shadowResolution;
    private SerializedDataParameter _shadowSize;
    private CloudMapParameters[] _layers;

    /// <summary>
    /// Кэширует сериализованные параметры инспектора.
    /// </summary>
    public override void OnEnable()
    {
        base.OnEnable();

        var propertyFetcher = new PropertyFetcher<CloudLayer>(serializedObject);
        _state = Unpack(propertyFetcher.Find(x => x.state));
        _opacity = Unpack(propertyFetcher.Find(x => x.opacity));
        _upperHemisphereOnly = Unpack(propertyFetcher.Find(x => x.upperHemisphereOnly));
        _layerCount = Unpack(propertyFetcher.Find(x => x.layers));
        _resolution = Unpack(propertyFetcher.Find(x => x.resolution));
        _planetRadius = Unpack(propertyFetcher.Find(x => x.planetRadius));
        _shadowMultiplier = Unpack(propertyFetcher.Find(x => x.shadowMultiplier));
        _shadowTint = Unpack(propertyFetcher.Find(x => x.shadowTint));
        _shadowResolution = Unpack(propertyFetcher.Find(x => x.shadowResolution));
        _shadowSize = Unpack(propertyFetcher.Find(x => x.shadowSize));
        _layers = new[]
        {
            UnpackCloudMap(propertyFetcher.Find(x => x.layerA)),
            UnpackCloudMap(propertyFetcher.Find(x => x.layerB)),
        };
    }

    /// <summary>
    /// Рисует инспектор Cloud Layer Volume.
    /// </summary>
    public override void OnInspectorGUI()
    {
        PropertyField(_state);
        PropertyField(_opacity);
        if (showAdditionalProperties)
        {
            PropertyField(_upperHemisphereOnly);
            PropertyField(_resolution);
            PropertyField(_planetRadius);
        }

        PropertyField(_layerCount);
        DrawCloudMap(_layers[0], "Layer A");
        if (_layerCount.value.intValue == (int)CloudMapMode.Double)
            DrawCloudMap(_layers[1], "Layer B");

        PropertyField(_shadowMultiplier);
        PropertyField(_shadowTint);
        if (showAdditionalProperties)
            PropertyField(_shadowResolution);
        PropertyField(_shadowSize);

        if (_layers[0].cloudMap.value.objectReferenceValue == null)
            EditorGUILayout.HelpBox("Layer A has no cloud map. A white fallback texture will be used.", MessageType.Info);
    }

    private CloudMapParameters UnpackCloudMap(SerializedProperty serializedProperty)
    {
        var propertyFetcher = new RelativePropertyFetcher<CloudLayer.CloudMap>(serializedProperty);
        return new CloudMapParameters
        {
            cloudMap = Unpack(propertyFetcher.Find(x => x.cloudMap)),
            opacities = new[]
            {
                Unpack(propertyFetcher.Find(x => x.opacityR)),
                Unpack(propertyFetcher.Find(x => x.opacityG)),
                Unpack(propertyFetcher.Find(x => x.opacityB)),
                Unpack(propertyFetcher.Find(x => x.opacityA)),
            },
            altitude = Unpack(propertyFetcher.Find(x => x.altitude)),
            rotation = Unpack(propertyFetcher.Find(x => x.rotation)),
            tint = Unpack(propertyFetcher.Find(x => x.tint)),
            exposure = Unpack(propertyFetcher.Find(x => x.exposure)),
            distortion = Unpack(propertyFetcher.Find(x => x.distortionMode)),
            scrollOrientation = Unpack(propertyFetcher.Find(x => x.scrollOrientation)),
            scrollSpeed = Unpack(propertyFetcher.Find(x => x.scrollSpeed)),
            flowmap = Unpack(propertyFetcher.Find(x => x.flowmap)),
            raymarching = Unpack(propertyFetcher.Find(x => x.lighting)),
            steps = Unpack(propertyFetcher.Find(x => x.steps)),
            thickness = Unpack(propertyFetcher.Find(x => x.thickness)),
            probeDimmer = Unpack(propertyFetcher.Find(x => x.ambientProbeDimmer)),
            castShadows = Unpack(propertyFetcher.Find(x => x.castShadows)),
        };
    }

    private void DrawCloudMap(CloudMapParameters cloudMap, string label)
    {
        DrawHeader(label);
        PropertyField(cloudMap.cloudMap);

        using (new IndentLevelScope())
        {
            for (int index = 0; index < cloudMap.opacities.Length; ++index)
                PropertyField(cloudMap.opacities[index]);
        }

        PropertyField(cloudMap.altitude);
        PropertyField(cloudMap.rotation);
        PropertyField(cloudMap.tint);
        PropertyField(cloudMap.exposure);
        PropertyField(cloudMap.distortion);

        if (cloudMap.distortion.value.intValue != (int)CloudDistortionMode.None)
        {
            using (new IndentLevelScope())
            {
                PropertyField(cloudMap.scrollOrientation);
                PropertyField(cloudMap.scrollSpeed);
                if (cloudMap.distortion.value.intValue == (int)CloudDistortionMode.Flowmap)
                    PropertyField(cloudMap.flowmap);
            }
        }

        PropertyField(cloudMap.raymarching);
        using (new IndentLevelScope())
        {
            PropertyField(cloudMap.steps);
            PropertyField(cloudMap.thickness);
            PropertyField(cloudMap.probeDimmer);
        }

        PropertyField(cloudMap.castShadows);
    }
}
