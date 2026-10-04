# Third-Party Notices

BIBIM AI for Revit is licensed under the Apache License 2.0 (see [LICENSE](LICENSE)).
It redistributes the third-party components listed below, each under its own license.
Versions are those referenced by `Bibim.Core/Bibim.Core.csproj` and
`Bibim.Core/frontend/package-lock.json` for v1.2.0.

## Shipped with every build

| Component | Version | License | Source |
|---|---|---|---|
| Microsoft.Web.WebView2 (Core / Wpf / WinForms) | 1.0.2903.40 | BSD-3-Clause-style Microsoft license (LICENSE.txt in the package) | https://www.nuget.org/packages/Microsoft.Web.WebView2 |
| Microsoft.CodeAnalysis.CSharp / Microsoft.CodeAnalysis (Roslyn) | 4.12.0 | MIT | https://github.com/dotnet/roslyn |
| Newtonsoft.Json | 13.0.3 | MIT | https://github.com/JamesNK/Newtonsoft.Json |

## Shipped with the .NET Framework 4.8 builds (Revit 2022–2024)

On .NET 8 / .NET 10 these assemblies come from the runtime that hosts Revit and are not redistributed.

| Component | License | Source |
|---|---|---|
| System.Security.Cryptography.ProtectedData 8.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Collections.Immutable | MIT | https://github.com/dotnet/runtime |
| System.Reflection.Metadata | MIT | https://github.com/dotnet/runtime |
| System.Memory | MIT | https://github.com/dotnet/runtime |
| System.Buffers | MIT | https://github.com/dotnet/runtime |
| System.Numerics.Vectors | MIT | https://github.com/dotnet/runtime |
| System.Runtime.CompilerServices.Unsafe | MIT | https://github.com/dotnet/runtime |
| System.Text.Encoding.CodePages | MIT | https://github.com/dotnet/runtime |
| System.Threading.Tasks.Extensions | MIT | https://github.com/dotnet/runtime |

## Bundled into the panel UI (`wwwroot/`)

| Component | Version | License | Source |
|---|---|---|---|
| React | 19.2.4 | MIT | https://github.com/facebook/react |
| React DOM | 19.2.4 | MIT | https://github.com/facebook/react |
| scheduler | 0.27.0 | MIT | https://github.com/facebook/react |

Build-time tools (Vite, TypeScript, xUnit, Inno Setup) are not redistributed.

## Installer payload

| Component | License | Source |
|---|---|---|
| Microsoft Edge WebView2 Runtime bootstrapper (`redist/MicrosoftEdgeWebview2Setup.exe`) | Microsoft Edge WebView2 Runtime distribution terms | https://developer.microsoft.com/microsoft-edge/webview2/ |

## Data read at runtime (not redistributed)

`RevitAPI.xml`, `RevitAPIUI.xml` and `RevitAPIIFC.xml` are read from the local Autodesk Revit
installation to build the in-memory API search index. They are Autodesk property and are
never copied into this repository or the installer.

---

### MIT License (applies to the MIT-licensed components above)

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

Copyright notices: Roslyn © .NET Foundation and Contributors; .NET runtime libraries
© .NET Foundation and Contributors; Newtonsoft.Json © James Newton-King; React © Meta
Platforms, Inc. and affiliates; WebView2 © Microsoft Corporation.
