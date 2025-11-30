# OwnAudioSharp Project Overview

This document provides a comprehensive overview of the OwnAudioSharp project, its architecture, and development conventions.

## Project Overview

OwnAudioSharp is a professional, cross-platform audio framework for .NET. Its primary goal is to provide a glitch-free, real-time audio processing experience by using a native C++ audio engine. This approach avoids the common pitfalls of .NET's Garbage Collector (GC) in real-time audio applications.

### Key Features

*   **Native C++ Audio Engine:** The default engine is a C++-based audio core that eliminates GC-related audio glitches and provides deterministic real-time performance. It uses PortAudio if available, otherwise it falls back to a bundled miniaudio library.
*   **Managed C# Engines:** The project also includes pure C# implementations for different platforms (Windows, Linux, macOS, Android), which are useful for development and testing.
*   **AI-Powered Features:**
    *   **Vocal Separation:** State-of-the-art vocal and instrumental track separation using ONNX neural networks.
    *   **Audio Mastering:** AI-driven mastering to match the spectral characteristics of a reference track.
    *   **Chord Detection:** Advanced musical chord recognition.
*   **Cross-Platform:** The framework is designed to work on Windows, macOS, Linux, Android, and iOS.

### Architecture

The project is structured into several layers:

*   **`Ownaudio.Core`:** This is the core of the framework, containing the cross-platform interfaces, audio decoders, and other fundamental components. It has no external dependencies.
*   **Platform-Specific Projects (`Ownaudio.Windows`, `Ownaudio.Linux`, `Ownaudio.macOS`, `Ownaudio.Android`, `Ownaudio.iOS`):** These projects provide the platform-specific implementations of the audio engine.
*   **`Ownaudio.Native`:** This project contains the native C++ audio engine and the necessary wrappers to interact with it from C#.
*   **`OwnaudioNET`:** This is the high-level API that provides easy access to the framework's features, including the AI-powered tools.
*   **`OwnaudioExamples`:** A collection of example projects demonstrating how to use the framework.
*   **`OwnAudioTests`:** Unit and integration tests for the framework.

## Building and Running

The project is a standard .NET solution and can be built and run using the .NET CLI or Visual Studio.

### Building the Solution

To build the entire solution, run the following command in the root directory:

```sh
dotnet build
```

### Running the Examples

To run one of the example projects, navigate to its directory and use the `dotnet run` command. For example, to run the desktop example:

```sh
cd OwnAudio/OwnaudioExamples/OwnaudioDesktopExample
dotnet run
```

### Using the NuGet Package

The easiest way to use OwnAudioSharp in your own project is to install the NuGet package:

```sh
dotnet add package OwnAudioSharp
```

## Development Conventions

### Project Structure

As described in the architecture section, the project is divided into several projects, each with a specific responsibility. When adding new features, it's important to identify the correct project to modify.

### Cross-Platform Development

The framework uses a combination of techniques to achieve cross-platform compatibility:

*   **`DefineConstants`:** The `Ownaudio.Core` project uses `DefineConstants` to define platform-specific symbols (e.g., `WINDOWS`, `LINUX`, `MACOS`, `ANDROID`, `IOS`). This allows for platform-specific code compilation using `#if` directives.
*   **Platform-Specific Projects:** Each platform has its own project that implements the platform-specific parts of the audio engine.
*   **Native Libraries:** The native libraries for PortAudio and miniaudio are included for each supported platform and architecture.

### Testing

The project includes a comprehensive test suite in the `OwnAudioTests` directory. When adding new features or fixing bugs, it's important to add or update the corresponding tests. To run the tests, use the following command:

```sh
dotnet test
```
