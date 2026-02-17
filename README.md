# plysnotes

Real-time AI background removal and cyberpunk HUD overlay for your webcam feed. Built with .NET 8.0 WPF.

Takes your camera input, runs ONNX segmentation models to separate you from the background, composites a custom background image behind you, and renders a cyberpunk-themed heads-up display with system info, reticle, scanlines, and audio waveform visualization.

845 lines of C# across 8 source files.

## Requirements

- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Webcam

## Quick Start

```
dotnet restore plusnot.csproj
dotnet run --project plusnot.csproj
```

The app opens a 1280x760 window showing your live camera feed with background removal and HUD overlay.

## Controls

| Button | Action |
|--------|--------|
| Camera | Opens DirectShow camera configuration dialog |
| BG Image | File picker for a custom background image (jpg/png/bmp) |
| Settings | Toggles the right-side parameter panel |
| Calibrate | 5-second countdown, captures 15 frames to build a reference background |

### Settings Panel

| Parameter | Range | What it does |
|-----------|-------|-------------|
| Model | MediaPipe / MODNet / SINet | Switches the segmentation ONNX model |
| Camera Resolution | 640x480 / 1280x720 | Restarts the pipeline at the selected resolution |
| **Mask Cleanup** | | |
| Edge Blur | 0-21 | Gaussian blur on the segmentation mask edges |
| Mask Threshold | 0-255 | Binary threshold on the mask (higher = stricter cutout) |
| Edge Erode | 0-15 | Morphological erosion to shrink the mask edges |
| **Scene Calibration** | | |
| Diff Threshold | 0-100 | Pixel-difference sensitivity vs reference background (higher = less sensitive) |
| Diff Spread | 0-10 | How much detected-change regions expand outward |
| ONNX Threads | 1-16 | Thread count for ONNX Runtime inference |
| Background Removal | on/off | Toggles segmentation entirely |
| HUD Overlay | on/off | Toggles the cyberpunk HUD |
| Pipeline Debug | on/off | Shows 4 debug thumbnails: AI, Cleanup, BG Diff, Final |

## Architecture

```
MainWindow (WPF Dispatcher)
    |
    v
FramePipeline ─── orchestrates everything
    |
    ├── CameraCapture ──── DirectShow webcam input (OpenCV)
    |       |
    |       v
    ├── BackgroundSegmenter ──── async ONNX inference thread
    |       |
    |       v
    ├── AudioCapture ──── 44.1kHz PCM ring buffer (NAudio)
    |       |
    |       v
    └── Compositor ──── composites frame + mask + background (SkiaSharp)
            |
            ├── HudRenderer ──── reticle, brackets, scanline, timer, system info
            └── WaveformRenderer ──── audio waveform at bottom of frame
```

### Thread Model

| Thread | Role | Priority |
|--------|------|----------|
| Dispatcher | WPF UI updates, bitmap blit to screen | Normal |
| FramePipeline | Camera read loop, frame composition, UI queue | Normal |
| Segmentation | Async ONNX model inference | BelowNormal |
| NAudio Callback | PCM audio capture into ring buffer | Normal |

### Data Flow

```
CameraCapture ──Mat(BGR)──> FramePipeline
FramePipeline ──Mat(BGR)──> BackgroundSegmenter
BackgroundSegmenter ──byte[]mask──> FramePipeline
AudioCapture ──float[256]──> FramePipeline
FramePipeline ──pixels+mask+waveform──> Compositor
Compositor ──SKCanvas──> HudRenderer
Compositor ──SKCanvas+float[]──> WaveformRenderer
Compositor ──WriteableBitmap──> MainWindow
```

### Segmentation Pipeline

1. Camera frame resized to model input size
2. BGR pixels normalized to float32 tensor (model-specific normalization)
3. ONNX inference produces float32 mask (0-1)
4. Mask extracted to byte[] (0-255)
5. Post-processing: morphological close (fill holes) -> open (remove noise) -> threshold -> erode -> Gaussian blur
6. Temporal smoothing: 70% current + 30% previous mask
7. Optional reference background diff combined with model mask via `max()`

## ONNX Models

| Model | File | Input Size | File Size | Notes |
|-------|------|-----------|-----------|-------|
| MediaPipe | `mediapipe_selfie.onnx` | 256x256 | 462 KB | Fastest, selfie-optimized, loaded first by default |
| MODNet | `modnet.onnx` | 512x512 | 25.8 MB | Highest quality, BGR normalization `(x-127.5)/127.5` |
| SINet | `sinet.onnx` | 320x320 | 438 KB | Balanced, uses channel index 1 for mask output |

Models are stored in `Models/` and copied to the output directory on build.

## Project Structure

```
plusnot.csproj              Project file (.NET 8.0, WPF, unsafe blocks)
App.xaml / App.xaml.cs      Application entry, toolbar/panel styles
MainWindow.xaml             UI layout: image display, tuning panel, toolbar
MainWindow.xaml.cs          UI event handlers, pipeline lifecycle

Pipeline/
  FramePipeline.cs          Orchestrator: camera loop, background capture, frame dispatch
  CameraCapture.cs          DirectShow webcam wrapper (OpenCV VideoCapture)
  BackgroundSegmenter.cs    ONNX inference, mask post-processing, reference background
  AudioCapture.cs           44.1kHz mono PCM capture into ring buffer
  PipelineDebugData.cs      Struct carrying intermediate mask references for debug thumbnails

Rendering/
  Compositor.cs             SkiaSharp offscreen composition, background blending, bitmap blit
  HudRenderer.cs            Corner brackets, reticle, scanline, timer, system readouts
  WaveformRenderer.cs       Audio waveform stroke + fill at bottom of frame

Models/
  mediapipe_selfie.onnx     MediaPipe selfie segmentation
  modnet.onnx               MODNet portrait matting
  sinet.onnx                SINet salient object detection

Assets/
  default_bg.jpg            Default background image
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| OpenCvSharp4 | 4.10.0.20241108 | Camera capture, image processing, morphological ops |
| OpenCvSharp4.runtime.win | 4.10.0.20241108 | Windows native OpenCV binaries |
| Microsoft.ML.OnnxRuntime | 1.20.1 | ONNX model inference for segmentation |
| SkiaSharp | 2.88.9 | 2D rendering, offscreen composition, HUD drawing |
| NAudio | 2.2.1 | Audio capture for waveform visualization |

## Build

```
dotnet build plusnot.csproj
```

Output: `bin/Debug/net8.0-windows/plusnot.exe`

### Publish a release

```
dotnet publish plusnot.csproj -c Release -r win-x64 --self-contained -o ./release
```

Produces a self-contained executable with all dependencies bundled.

## Developer Dashboard

Open `plysnotes-dashboard.html` in a browser for a visual command runner, release management UI, and architecture visualization. All UI is generated from data structures that mirror the application architecture described above.
