# Image Viewer — agent entry point

**The canonical handover document is [`CLAUDE.md`](CLAUDE.md). Read it before changing anything.**
This file exists only so Codex has an auto-loaded entry point; do not duplicate content here.

## The five things most likely to bite you

1. **Never reference ImageSharp, SkiaSharp or Magick.NET from a field, constructor or method
   signature.** Startup speed depends on the CLR never loading those ~55 MB of decoders unless an
   exotic file is opened, which only holds while every reference stays inside a method body. Run
   `--assembly-check` (in its own process) after touching `src/ImageViewer/Imaging`.

2. **`JpegBitmapEncoder.Rotation` is not lossless** — measured, despite its reputation. JPEG rotation
   goes through `JpegOrientationWriter`, which patches EXIF bytes and never touches the compressed
   data. Do not "simplify" it back to the encoder.

3. **ImageSharp is pinned to 2.1.13 on purpose.** 3.0 changed to a paid licence. Upgrading needs a
   licensing decision, not just a version bump.

4. **Cold start is ~1.0–1.2 s and that is mostly WPF itself** (an empty WPF window measures ~945 ms
   on this machine). The single-instance pipe, not startup optimisation, is what makes the app feel
   instant. Do not chase the obsolete 300 ms figure from the original plan.

5. **A superseded decode can still complete.** Native decoders ignore cancellation, so anything that
   publishes a decode result must first check `IsStillCurrent(path)`.

## Commands

```bash
dotnet build "src/ImageViewer/ImageViewer.csproj"
```

```bash
dotnet run --project "tests/ImageViewer.SelfTest/ImageViewer.SelfTest.csproj" -- <test-image-folder>
```

```bash
pwsh -File packaging/build-portable.ps1
```

Generate the exotic-format test corpus once with `-- --make-corpus <folder>` before the main suite,
otherwise the fallback decoder tiers are never exercised.
