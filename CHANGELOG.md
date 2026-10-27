# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
