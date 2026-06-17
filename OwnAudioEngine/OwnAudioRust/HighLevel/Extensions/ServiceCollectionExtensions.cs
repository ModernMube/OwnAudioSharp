using System;
using Microsoft.Extensions.DependencyInjection;

namespace Ownaudio.Audio.Extensions;

/// <summary>
/// Extension methods for registering OwnAudio services in a
/// <see cref="IServiceCollection"/> (Microsoft.Extensions.DependencyInjection).
/// </summary>
/// <remarks>
/// This class is kept in the main assembly for convenience.  A future release may move it
/// to a separate <c>Ownaudio.Audio.Extensions.DependencyInjection</c> NuGet package so that
/// the core package does not carry a dependency on <c>Microsoft.Extensions.*</c> for
/// applications that do not use DI containers.
/// </remarks>
public static class ServiceCollectionExtensions
{
    #region Public methods

    /// <summary>
    /// Registers <see cref="AudioEngine"/> as a singleton in the DI container.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">
    /// Optional delegate to configure <see cref="AudioEngineOptions"/> before engine creation.
    /// </param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// The <see cref="AudioEngine"/> singleton is created lazily on first resolution.
    /// Dispose the DI container (or the engine service) to properly shut down the audio engine.
    /// </remarks>
    public static IServiceCollection AddOwnAudio(
        this IServiceCollection services,
        Action<AudioEngineOptions>? configure = null)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddSingleton<AudioEngine>(sp =>
        {
            var options = new AudioEngineOptions();
            configure?.Invoke(options);
            return AudioEngine.Create(options);
        });

        return services;
    }

    #endregion
}
