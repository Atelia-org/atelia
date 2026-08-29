namespace Atelia.MemoPod;

public sealed partial class MemoPod {
    public Task FreezeAsync(CancellationToken cancellationToken = default) {
        ThrowIfInvalidated();
        RequirePhase(MemoPodPhase.Editable, nameof(FreezeAsync));
        cancellationToken.ThrowIfCancellationRequested();

        MemoPodDocument candidate = _working.CaptureDocument();
        _testHooks.BeforeRender?.Invoke(candidate);
        MemoPodFrozenPrompt prompt = MemoPodPromptRenderer.Render(candidate);
        _testHooks.AfterRenderBeforePublish?.Invoke(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dirty) {
            MemoPodPublishResult result = MemoPodDocumentPublisher.Publish(
                _rootPath,
                candidate,
                _nextPublishMode,
                _testHooks.PublisherHooks,
                cancellationToken
            );
            switch (result.Settlement) {
                case MemoPodPublishSettlement.NotPublished:
                    throw MemoPodPersistenceErrors.FromPublishFailure(
                        result.Failure
                    );
                case MemoPodPublishSettlement.CommitIndeterminate:
                    _invalidated = true;
                    _frozenPrompt = null;
                    throw MemoPodPersistenceErrors.CommitIndeterminate(
                        result.Failure
                    );
                case MemoPodPublishSettlement.Published:
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown MemoPod publish settlement '{result.Settlement}'."
                    );
            }
        }

        // After a proven Published settlement, this method deliberately has
        // no cancellation observation or fallible callback.
        _nextPublishMode = MemoPodPublishMode.ReplaceExisting;
        _dirty = false;
        _frozenPrompt = prompt;
        _phase = MemoPodPhase.Frozen;
        return Task.CompletedTask;
    }

    public void ResumeEditing() {
        ThrowIfInvalidated();
        RequirePhase(MemoPodPhase.Frozen, nameof(ResumeEditing));

        _frozenPrompt = null;
        _dirty = false;
        _phase = MemoPodPhase.Editable;
    }
}
