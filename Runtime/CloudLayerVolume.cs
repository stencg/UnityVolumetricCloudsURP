using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Количество карт, используемых слоем облаков.
/// </summary>
public enum CloudMapMode
{
    /// <summary>Один слой.</summary>
    Single,
    /// <summary>Два слоя.</summary>
    Double,
}

/// <summary>
/// Режим искажения карты облаков.
/// </summary>
public enum CloudDistortionMode
{
    /// <summary>Без искажения.</summary>
    None,
    /// <summary>Горизонтальное процедурное движение.</summary>
    [InspectorName("Horizontal")]
    Procedural,
    /// <summary>Искажение с помощью flow map.</summary>
    Flowmap,
}

/// <summary>
/// Разрешение предварительно рассчитанной текстуры облаков.
/// </summary>
public enum CloudResolution
{
    /// <summary>256 пикселей.</summary>
    CloudResolution256 = 256,
    /// <summary>512 пикселей.</summary>
    CloudResolution512 = 512,
    /// <summary>1024 пикселя.</summary>
    CloudResolution1024 = 1024,
    /// <summary>2048 пикселей.</summary>
    CloudResolution2048 = 2048,
    /// <summary>4096 пикселей.</summary>
    CloudResolution4096 = 4096,
    /// <summary>8192 пикселя.</summary>
    CloudResolution8192 = 8192,
}

/// <summary>
/// Разрешение cookie облачных теней.
/// </summary>
public enum CloudShadowsResolution
{
    /// <summary>Очень низкое качество.</summary>
    VeryLow = 64,
    /// <summary>Низкое качество.</summary>
    Low = 128,
    /// <summary>Среднее качество.</summary>
    Medium = 256,
    /// <summary>Высокое качество.</summary>
    High = 512,
}

#if UNITY_2023_1_OR_NEWER
[Serializable, VolumeComponentMenu("Sky/Cloud Layer (URP)"), SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
#else
[Serializable, VolumeComponentMenuForRenderPipeline("Sky/Cloud Layer (URP)", typeof(UniversalRenderPipeline))]
#endif
public sealed class CloudLayer : VolumeComponent, IPostProcessComponent
{
    /// <summary>Включает отрисовку Cloud Layer.</summary>
    [Header("General"), Tooltip("Enable/Disable the cloud layer effect.")]
    public BoolParameter state = new(false, BoolParameter.DisplayType.EnumPopup, overrideState: true);

    /// <summary>Общая непрозрачность облаков.</summary>
    [Tooltip("Controls the global opacity of the cloud layer.")]
    public ClampedFloatParameter opacity = new(1.0f, 0.0f, 1.0f);

    /// <summary>Ограничивает облака верхней полусферой.</summary>
    [AdditionalProperty, Tooltip("Check this box if the cloud layer covers only the upper part of the sky.")]
    public BoolParameter upperHemisphereOnly = new(true);

    /// <summary>Количество слоёв облаков.</summary>
    [Tooltip("Specifies whether one or two cloud maps are rendered.")]
    public VolumeParameter<CloudMapMode> layers = new();

    /// <summary>Разрешение рассчитанной текстуры облаков.</summary>
    [AdditionalProperty, Tooltip("Specifies the resolution of the precomputed cloud texture.")]
    public EnumParameter<CloudResolution> resolution = new(CloudResolution.CloudResolution1024);

    /// <summary>Радиус планеты, используемый для кривизны слоя.</summary>
    [AdditionalProperty, Tooltip("Specifies the planet radius used to curve the cloud layer.")]
    public MinFloatParameter planetRadius = new(6378100.0f, 1.0f);

    /// <summary>Интенсивность облачных теней.</summary>
    [Header("Cloud Shadows"), Tooltip("Controls the opacity of the cloud shadows.")]
    public MinFloatParameter shadowMultiplier = new(1.0f, 0.0f);

    /// <summary>Цвет облачных теней.</summary>
    [Tooltip("Controls the tint of the cloud shadows.")]
    public ColorParameter shadowTint = new(Color.black, false, false, true);

    /// <summary>Разрешение cookie облачных теней.</summary>
    [AdditionalProperty, Tooltip("Specifies the resolution of the cloud shadow cookie.")]
    public EnumParameter<CloudShadowsResolution> shadowResolution = new(CloudShadowsResolution.Medium);

    /// <summary>Размер проекции облачных теней в мировых единицах.</summary>
    [Tooltip("Specifies the size of the projected cloud shadows.")]
    public MinFloatParameter shadowSize = new(500.0f, 0.0f);

    /// <summary>
    /// Параметры одной карты облаков.
    /// </summary>
    [Serializable]
    public sealed class CloudMap
    {
        /// <summary>Карта облаков в проекции latitude-longitude.</summary>
        [Tooltip("Specify the texture used to render the clouds (in LatLong layout).")]
        public Texture2DParameter cloudMap = new(null);

        /// <summary>Вес красного канала.</summary>
        [Tooltip("Opacity multiplier for the red channel.")]
        public ClampedFloatParameter opacityR = new(1.0f, 0.0f, 1.0f);

        /// <summary>Вес зелёного канала.</summary>
        [Tooltip("Opacity multiplier for the green channel.")]
        public ClampedFloatParameter opacityG = new(0.0f, 0.0f, 1.0f);

        /// <summary>Вес синего канала.</summary>
        [Tooltip("Opacity multiplier for the blue channel.")]
        public ClampedFloatParameter opacityB = new(0.0f, 0.0f, 1.0f);

        /// <summary>Вес альфа-канала.</summary>
        [Tooltip("Opacity multiplier for the alpha channel.")]
        public ClampedFloatParameter opacityA = new(0.0f, 0.0f, 1.0f);

        /// <summary>Высота нижней границы слоя.</summary>
        [Tooltip("Altitude of the bottom of the cloud layer in meters.")]
        public MinFloatParameter altitude = new(2000.0f, 0.0f);

        /// <summary>Поворот карты облаков.</summary>
        [Tooltip("Sets the rotation of the clouds in degrees.")]
        public ClampedFloatParameter rotation = new(0.0f, 0.0f, 360.0f);

        /// <summary>Цветовой множитель облаков.</summary>
        [Tooltip("Specifies the color used to tint the clouds.")]
        public ColorParameter tint = new(Color.white, false, false, true);

        /// <summary>Компенсация экспозиции слоя.</summary>
        [InspectorName("Exposure Compensation"), Tooltip("Sets the exposure compensation of the clouds in EV.")]
        public FloatParameter exposure = new(0.0f);

        /// <summary>Режим движения облаков.</summary>
        [InspectorName("Wind"), Tooltip("Distortion mode used to simulate cloud movement.")]
        public VolumeParameter<CloudDistortionMode> distortionMode = new();

        /// <summary>Направление ветра относительно мировой оси X.</summary>
        [InspectorName("Orientation"), Tooltip("Controls the wind orientation relative to the world X axis.")]
        public ClampedFloatParameter scrollOrientation = new(0.0f, 0.0f, 360.0f);

        /// <summary>Скорость ветра в километрах в час.</summary>
        [InspectorName("Speed"), Tooltip("Sets the wind speed in kilometers per hour.")]
        public MinFloatParameter scrollSpeed = new(0.0f, 0.0f);

        /// <summary>Карта векторов движения.</summary>
        [Tooltip("Specify the flow map used for cloud distortion (in LatLong layout).")]
        public Texture2DParameter flowmap = new(null);

        /// <summary>Включает расчёт собственного затенения.</summary>
        [InspectorName("Raymarching"), Tooltip("Simulates cloud self-shadowing using raymarching.")]
        public BoolParameter lighting = new(true);

        /// <summary>Количество шагов собственного затенения.</summary>
        [Tooltip("Number of raymarching steps.")]
        public ClampedIntParameter steps = new(6, 2, 32);

        /// <summary>Плотность слоя облаков.</summary>
        [InspectorName("Density"), Tooltip("Density of the cloud layer.")]
        public ClampedFloatParameter thickness = new(0.5f, 0.0f, 1.0f);

        /// <summary>Множитель освещения от ambient probe.</summary>
        [Tooltip("Controls the influence of the ambient probe on the cloud layer.")]
        public ClampedFloatParameter ambientProbeDimmer = new(1.0f, 0.0f, 1.0f);

        /// <summary>Включает проекцию теней от слоя.</summary>
        [Tooltip("Projects cloud shadows and overrides the main directional light cookie for the current camera.")]
        public BoolParameter castShadows = new(false);

        internal int NumSteps => lighting.value ? steps.value : 0;
        internal Vector4 Opacities => new(opacityR.value, opacityG.value, opacityB.value, opacityA.value);
        internal Color Color => tint.value * ColorUtils.ConvertEV100ToExposure(-exposure.value);

        internal Vector4 GetRenderingParameters(float scrollFactor)
        {
            float angle = Mathf.Deg2Rad * scrollOrientation.value;
            return new Vector4(-Mathf.Cos(angle), -Mathf.Sin(angle), scrollFactor, 0.0f);
        }

        internal void GetBakingParameters(out Vector4 opacities, out Vector4 parameters)
        {
            opacities = Opacities;
            parameters = new Vector4(
                rotation.value / 360.0f,
                NumSteps,
                Mathf.Lerp(0.005f, 0.1f, thickness.value),
                altitude.value);
        }

        internal int GetBakingHashCode()
        {
            int hash = 17;
            unchecked
            {
                hash = hash * 23 + cloudMap.GetHashCode();
                hash = hash * 23 + opacityR.GetHashCode();
                hash = hash * 23 + opacityG.GetHashCode();
                hash = hash * 23 + opacityB.GetHashCode();
                hash = hash * 23 + opacityA.GetHashCode();
                hash = hash * 23 + rotation.GetHashCode();
                hash = hash * 23 + castShadows.GetHashCode();

                if (lighting.value)
                {
                    hash = hash * 23 + lighting.GetHashCode();
                    hash = hash * 23 + steps.GetHashCode();
                    hash = hash * 23 + altitude.GetHashCode();
                    hash = hash * 23 + thickness.GetHashCode();
                }
            }

            return hash;
        }
    }

    /// <summary>Первый слой облаков.</summary>
    public CloudMap layerA = new();

    /// <summary>Второй слой облаков.</summary>
    public CloudMap layerB = new();

    internal int NumLayers => layers.value == CloudMapMode.Single ? 1 : 2;
    internal bool CastShadows => layerA.castShadows.value ||
        (layers.value == CloudMapMode.Double && layerB.castShadows.value);

    internal int GetBakingHashCode(Light sunLight)
    {
        int hash = 17;
        bool lighting = layerA.lighting.value;
        bool shadows = sunLight != null && layerA.castShadows.value;

        unchecked
        {
            hash = hash * 23 + upperHemisphereOnly.GetHashCode();
            hash = hash * 23 + layers.GetHashCode();
            hash = hash * 23 + resolution.GetHashCode();
            hash = hash * 23 + planetRadius.GetHashCode();
            hash = hash * 23 + layerA.GetBakingHashCode();

            if (layers.value == CloudMapMode.Double)
            {
                hash = hash * 23 + layerB.GetBakingHashCode();
                lighting |= layerB.lighting.value;
                shadows |= layerB.castShadows.value;
            }

            if (lighting && sunLight != null)
            {
                Vector3 angles = sunLight.transform.rotation.eulerAngles;
                hash = hash * 23 + new Vector3Int((int)angles.x, (int)angles.y, (int)angles.z).GetHashCode();
            }

            if (shadows)
                hash = hash * 23 + shadowResolution.GetHashCode();
        }

        return hash;
    }

    /// <summary>
    /// Проверяет, должен ли эффект отрисовываться.
    /// </summary>
    /// <returns><see langword="true"/>, если Cloud Layer включён и видим.</returns>
    public bool IsActive() => state.value && opacity.value > 0.0f;

    /// <summary>
    /// Возвращает совместимость с tile-based post-processing.
    /// </summary>
    /// <returns>Всегда <see langword="false"/>.</returns>
    public bool IsTileCompatible() => false;
}
