using System.Collections.Concurrent;

namespace ChallengeEditor;

/// Minimal main-thread message pump. Background tasks <see cref="Post"/> a
/// delegate from any thread; <see cref="Drain"/> runs every queued delegate
/// in order on the calling thread (always the ImGui/Veldrid thread).
///
/// Used for GLB import (parse on threadpool → upload meshes on main thread)
/// and any future ArenaBuilder long-running build that needs to update scene
/// state when it finishes. Veldrid resources (DeviceBuffer.Create, etc.) are
/// device-scoped and must be created on the render thread, which is why we
/// can't just mutate <c>EditorScene</c> from inside the continuation.
public sealed class MainThreadDispatcher
{
    private readonly ConcurrentQueue<Action> _pending = new();

    /// <summary>Enqueue work to run on the next <see cref="Drain"/>. Safe from any thread.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _pending.Enqueue(work);
    }

    /// <summary>Run every queued delegate on the calling thread. Exceptions in
    /// one delegate do not stop later ones — the failure is surfaced via
    /// <see cref="LastException"/> so the UI can show it.</summary>
    public void Drain()
    {
        while (_pending.TryDequeue(out Action? work))
        {
            try { work(); }
            catch (Exception ex) { LastException = ex; }
        }
    }

    /// <summary>Most recent unhandled exception from a drained delegate, or null.
    /// Cleared by callers after they've surfaced it.</summary>
    public Exception? LastException { get; set; }
}
