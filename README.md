UnityVolumetricCloudsURP
=============
 
 Volumetric Clouds and Cloud Layer for Unity URP (Universal Render Pipeline). The rendering is ported from HDRP.
 
 **Please read the Documentation and Requirements before using this repository.**
 
Screenshots
------------
**Sample Scene**
 
Global Volumetric Clouds:
 
 ![GlobalVolumetricClouds](./Documentation/Images/Showcases/GlobalClouds.jpg)
 
**Not Included**
 
Local Volumetric Clouds:
 
 ![LocalVolumetricClouds1](./Documentation~/Images/Showcases/LocalClouds1.jpg)
 
 ![LocalVolumetricClouds2](./Documentation~/Images/Showcases/LocalClouds2.jpg)
 
[Physically Based Sky](https://github.com/stencg/UnityPhysicallyBasedSkyURP):
 
 ![PhysicallyBasedSkyURP](./Documentation~/Images/Showcases/PhysicallyBasedSky.jpg)
 
Documentation
------------
- [How to setup](./Documentation~/Setup.md)

Cloud Layer setup
------------
1. Add **Cloud Layer URP** to the active Universal Renderer Data.
2. Add **Cloud Layer (URP)** to a Volume profile and enable **State**.
3. Assign a latitude-longitude cloud map to Layer A (and Layer B when using Double mode).

Cloud Layer is implemented with Unity 6 RenderGraph only. Its shadow option uses the main directional-light cookie, so **Light Cookies** must be enabled in the active URP asset.
 
Requirements
------------
- Unity 2022.2 and URP 14 or above.
- Shader model 3.5 or above (at leaset OpenGL ES 3.0 or equivalent)
 
Reminders
------------
- Some settings are still WIP, such as **Custom Cloud Map** overrides.
- Orthographic camera is not supported.
- To render volumetric clouds shadows, it will override the cookie in the main directional light.
- To customize the **planet radius and center** for volumetric clouds, install [Physically Based Sky](https://github.com/stencg/UnityPhysicallyBasedSkyURP) via the package manager.
 
Scripting Define Symbols
------------
Some features are gated behind C# preprocessor defines. Add the ones you want to
**Project Settings → Player → Other Settings → Scripting Define Symbols** (or via an `.asmdef`
*Define Constraints* / script). After changing them, allow Unity to recompile.

### Manual defines (set by the user)

| Define | Default | Description |
| --- | --- | --- |
| `ZERO` | **Recommended / required** | Enables the core **Render Graph** implementation that acquires the active camera color and depth textures from the frame data (`UniversalResourceData.activeColorTexture` / `activeDepthTexture` / `cameraDepthTexture`) and allocates the clouds render textures. Without `ZERO` the render-graph passes do not bind the camera targets, so volumetric clouds will not render on Unity 6's Render Graph. **This is the most important define and should always be set.** |
| `VC_SHADOWS` | Off | Enables the **VolumetricCloudsShadowsPass**, which bakes volumetric cloud shadows into the main directional light's cookie so clouds cast shadows onto the scene. Requires **Light Cookies** to be enabled in the active URP asset (the cookie of the main directional light is overridden). |
| `VC_DENOISE` | Off | Enables **temporal reprojection denoising** for the clouds. When active and `Denoise Clouds` is enabled, allocates accumulation/history textures and runs the temporal-reprojection combine pass for a smoother, more stable result at low resolution scales. |
| `VC_DEPTH` | Off | Enables **depth** handling: reading the camera depth texture, outputting the clouds average depth to the global `_VolumetricCloudsDepthTexture`, and (when *Output To Scene Depth* is on) writing the clouds depth back into `_CameraDepthTexture`. Required for any depth-based cloud feature. |
| `VC_AMBIENT` | Off | Enables the **VolumetricCloudsAmbientPass**, which renders a dynamic ambient probe (cube map) used for clouds ambient lighting. |
| `OPTIMISATION` | Off | Enables performance optimizations: `[BurstCompile]` on math jobs (when Burst is present), `NativeArray`/`GetRawTextureData` instead of managed `Color[]` arrays, a cached compiled delegate for reflection-based depth-texture access, and `AggressiveInlining` on hot helpers. |
| `_CONST_EARTH_RADIUS` | Off | Treats the Earth radius as a compile-time `const` (skipping the per-material `_EarthRadius` property lookup) and marks the dependent functions `static`. Use only when the Earth radius is always the default value; disable it when the radius can be changed at runtime (e.g. via Physically Based Sky). |
| `UNUSED` | Off | Keeps currently-dead/experimental code paths compiled in (e.g. `SetGlobalTextureAfterPass` calls, the `PowderEffect` function and the standalone ray-sphere intersection helper). Leave it off for normal builds to strip unused code. |

> The default column reflects the *recommended* state when you first install the package;
> none of these are set automatically, so add them explicitly.

### Automatically defined (via `versionDefines` in the `.asmdef`)

These are set for you by the assembly definition based on which packages are installed, so you
normally do **not** add them yourself.

| Define | Auto-set when | Description |
| --- | --- | --- |
| `URP_PBSKY` | [Physically Based Sky URP](https://github.com/jiaozi158/UnityPhysicallyBasedSkyURP) (`com.jiaozi158.unity-physically-based-sky-urp`) ≥ 1.0.0 is installed | Integrates with Physically Based Sky: reads the planet center/radius and rendering space from `VisualEnvironment`, enables local clouds, atmospheric-scattering blending and the dynamic ambient probe from PBR sky. |
| `URP_PHYSICAL_LIGHT` | Physical Light Unit (`cn.unity.physical-light-unit`) ≥ 0.0.1 is installed | Adjusts the sun color computation so that lights using physical units (with `AdditionalLightData`) are not double-multiplied by π. |
| `ENABLE_BURST_1_0_0_OR_NEWER` | Burst (`com.unity.burst`) ≥ 1.0.0 is installed | Allows `[BurstCompile]` attributes to take effect (used together with `OPTIMISATION`). |
| `URP_COMPATIBILITY_MODE` | URP (`com.unity.render-pipelines.universal`) is in range `[1.0.0, 14.0.12]` (Unity 2022 / URP 14) | Enables the legacy non-Render-Graph `OnCameraSetup` / `Execute(ScriptableRenderContext, ref RenderingData)` callbacks and reflection-based access to `UniversalRenderer.m_DepthTexture`. Not used on Unity 6.4+, where the compatibility-mode callbacks were removed. |

### Recommended combination

- **Unity 6 (Render Graph):** `ZERO` (minimum). Add `VC_SHADOWS`, `VC_DENOISE`, `VC_DEPTH`,
  `VC_AMBIENT` and `OPTIMISATION` as needed.

License
------------
MIT
![MIT License](http://img.shields.io/badge/license-MIT-blue.svg?style=flat)
 
Details
------------
Please refer to [HDRP Volumetric Clouds Documentation](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Override-Volumetric-Clouds.html#properties).
