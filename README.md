# NitroSharp (codename Project Hoppy)

Committee of Zero's effort to reimplement n2system, a visual novel engine used in a number of games made by Nitroplus. The effort is primarily focused on making the entirety of Chaos;Head Noah, a console-exclusive game, fully playable on PC (Windows, Linux) and potentially other platforms (Android, macOS).

## Building
### Required Software
- [.NET Core SDK 3.1.302](https://dotnet.microsoft.com/download/dotnet-core/3.1)
- [Vulkan SDK](https://www.lunarg.com/vulkan-sdk/) (might not be a hard requirement)

Run
```
dotnet run --no-launch-profile --project ./src/NitroSharp.ShaderCompiler/NitroSharp.ShaderCompiler.csproj ./src/NitroSharp/Graphics/Shaders ./bin/obj/NitroSharp/Shaders.Generated

dotnet build NitroSharp.sln [-c Release]
```
Alternatively, you can run ``aot-build.ps1`` in PowerShell 6/7 to produce an AOT-compiled build.
