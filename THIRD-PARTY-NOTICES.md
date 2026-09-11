# Third-party notices

This project depends on the components below. All are under permissive licences; none
imposes copyleft obligations on this project's own source.

If you redistribute a **built** copy of this app (the `dotnet publish` output, a zip, a
release asset), that output contains the compiled dependencies, and their licences require
their copyright notices to travel with it. Shipping this file alongside the binaries
satisfies that.

## NuGet packages

| Package | Version | Licence | Copyright |
|---|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | MIT | © 2020 Mark Heath |
| [org.k2fsa.sherpa.onnx](https://github.com/k2-fsa/sherpa-onnx) | 1.13.5 | Apache-2.0 | © The sherpa-onnx authors |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | 1.9.1 | MIT | © 2024 sandrohanea |
| [Whisper.net.Runtime](https://github.com/sandrohanea/whisper.net) | 1.9.1 | MIT | © 2024 sandrohanea |
| `Microsoft.Extensions.*` (Configuration, DependencyInjection, Logging, Options, Http) | 8.x | MIT | © Microsoft Corporation |

Apache-2.0 additionally asks that you state significant changes if you distribute a modified
version of that component. This project consumes sherpa-onnx as an unmodified NuGet package,
so there is nothing to state.

## Speech models

**No model weights are distributed with this project.** `models/` is excluded from source
control, and `scripts/download-models.ps1` fetches a model on your machine at your request.
Each model archive carries its own licence file, which stays in the model directory.

| Model | Licence | Copyright |
|---|---|---|
| Moonshine Base / Tiny (en) — the default | MIT | © 2024 Useful Sensors |
| NVIDIA Parakeet TDT | CC-BY-4.0 (check the archive) | © NVIDIA |
| whisper.cpp GGML builds | MIT | © the whisper.cpp authors |

If you publish a build with a model bundled in, that model's licence and attribution have to
travel with it too. Check the archive rather than trusting this table — the upstream terms
are what apply, and model licences change more often than code licences.

## .NET runtime

The published app requires the .NET 8 Desktop Runtime (MIT, © Microsoft Corporation), either
installed on the machine or included by `--self-contained`.
