# Changelog

## 2026-02-17 — Pipeline debug thumbnails and scene calibration controls

### Added
- **Pipeline debug thumbnails**: toggleable overlay showing 4 intermediate masks (AI, Cleanup, BG Diff, Final) so you can see what each pipeline stage is doing while tuning parameters
- **Diff Threshold slider** (0-100): controls pixel-difference sensitivity for background detection (was hardcoded at 25)
- **Diff Spread slider** (0-10): controls how much background-diff regions expand outward (was hardcoded at kernel 5)
- **Scene Calibration** section header in settings panel to group bg-diff controls
- **Mask Cleanup** section header in settings panel to group mask post-processing controls

### Changed
- Calibration countdown extended from 3s to 5s for more time to step away
- Toolbar buttons renamed: Camera, BG Image, Settings, Calibrate (was Cam Settings, Change Background, Tuning, Capture BG)
- Settings panel title changed from "Tuning" to "Settings"
- README updated to reflect new UI names and parameters

### Files changed
- `Pipeline/PipelineDebugData.cs` — new struct for debug mask references
- `Pipeline/BackgroundSegmenter.cs` — debug capture, DiffThreshold/DiffDilate properties
- `Pipeline/FramePipeline.cs` — PipelineDebugEnabled, DiffThreshold, DiffDilate passthrough
- `Rendering/Compositor.cs` — debug thumbnail rendering (DrawDebugThumbnails, DrawGrayscaleThumbnail)
- `MainWindow.xaml` — settings panel reorganization, new sliders/checkbox, toolbar renames
- `MainWindow.xaml.cs` — handlers for new controls, 5s countdown
- `README.md` — updated docs
