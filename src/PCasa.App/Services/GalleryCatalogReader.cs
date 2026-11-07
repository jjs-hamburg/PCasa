namespace PCasa.App.Services;

/// <summary>
/// Bounded read-only capacity for the foreground catalog projection.
/// These reads do not enter the scheduler's P0-P4 task queue or journal.
/// Callers still own cancellation, view revisions, and WPF publication.
/// This class never writes catalog data, thumbnails, or original files.
/// </summary>
internal sealed class GalleryCatalogReader
{
    private readonly SemaphoreSlim _capacity = new(2, 2);

    public async Task ReadAsync(
        Func<CancellationToken, Task> operation,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isCurrent is not null && !isCurrent())
                    throw new OperationCanceledException(cancellationToken);
                await operation(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (isCurrent is not null && !isCurrent())
                    throw new OperationCanceledException(cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _capacity.Release();
        }
    }

    public async Task<T> ReadAsync<T>(
        Func<CancellationToken, T> operation,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isCurrent is not null && !isCurrent())
                    throw new OperationCanceledException(cancellationToken);
                var result = operation(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (isCurrent is not null && !isCurrent())
                    throw new OperationCanceledException(cancellationToken);
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _capacity.Release();
        }
    }
}
