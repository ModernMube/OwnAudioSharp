#!/bin/bash
# Build script for creating multi-platform NuGet package

# Clean previous builds
dotnet clean -c Release

# Build each platform-specific project
echo "Building Windows project..."
dotnet build ../../OwnAudioEngine/Ownaudio.Windows/Ownaudio.Windows.csproj -c Release

echo "Building Linux project..."
dotnet build ../../OwnAudioEngine/Ownaudio.Linux/Ownaudio.Linux.csproj -c Release

echo "Building macOS project..."
dotnet build ../../OwnAudioEngine/Ownaudio.macOS/Ownaudio.macOS.csproj -c Release

# Build the main project
echo "Building main project..."
dotnet build OwnaudioNET.csproj -c Release

# Create NuGet package
echo "Creating NuGet package..."
dotnet pack OwnaudioNET.csproj -c Release -o ./nupkg

echo "Package created in ./nupkg directory"
