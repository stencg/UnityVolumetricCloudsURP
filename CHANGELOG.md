# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.15] - 2026-11-05

### Fixed

- Applied Reflection Probe eligibility to dynamic glossy cubemaps and supplied mean cloud distance for environment atmosphere compositing.

- Made the environment cloud pass available for local packages on Android and shared effective camera cloud settings with environment capture.

## [1.0.14] - 2026-11-05

### Fixed

- Made PBSky assembly integration optional so volumetric clouds can be installed independently.
- Isolated volumetric-cloud sun direction and color from URP main-light globals, preventing baked sun lighting from leaking into transparent objects. Cloud lighting and shadow cookie behavior are preserved.
- Disabled cloud-depth sampling when VC_DEPTH is not compiled, preserving the far-depth fog fallback instead of reading an unbound texture.

## [1.0.13] - 2026-10-31

### Added

- Optional camera-relative horizontal fade for cloud shadow casters, with overridable start/width angles after Fade In. Defaults off; preserves existing Fade In, visible clouds, cookie filtering/opacity and receiver GI controls.
- Per-camera non-jittered asymmetric projection and pass-time parameter binding, without shader variants. Camera rotation intentionally changes off-screen shadow coverage.

## [1.0.12] - 2026-10-27

### Fixed

- Fixed renderer initialization exceptions when the URP asset Inspector creates renderers before the volume stack is available, including with Scene and Game views closed.
- Deferred cloud activity and shader keyword updates to camera pass scheduling, and safely skipped scheduling when the volume stack is unavailable.

## [1.0.11] - 2026-10-09

### Added

- Added dynamic ambient lighting for volumetric clouds and cloud layers.
- Added physically based sky atmospheric scattering to cloud layers.

### Changed

- Reworked RenderGraph resource handling and removed Compatibility Mode support.
- Moved physically based sky passes into the Physically Based Sky package.
- Skipped the intermediate cloud texture when volumetric-cloud raymarching is inactive.

### Fixed

- Fixed fog-only cloud rendering with None and Custom sky types.
- Required atmospheric LUTs only for physically based scattering and safely skipped invalid optional LUT handles.
