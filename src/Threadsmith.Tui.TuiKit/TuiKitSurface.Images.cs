namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using TUIKit.Modals;

/// <summary>Composer attachments share input ownership, session identity and epoch checks with text.</summary>
internal sealed partial class TuiKitSurface
{
    private readonly IImageArtifactStore? _imageArtifacts;
    private readonly Func<CancellationToken, Task<ClipboardCapture>> _captureClipboard;
    private readonly List<ImageArtifact> _draftImages = [];
    private SessionId? _attachmentSession;
    private IDisposable? _imageSessionLease;
    private InteractionInput? _failedImageSubmission;

    /// <inheritdoc />
    public Task RestoreSubmissionAsync(InteractionInput input, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(
            () =>
        {
            if (input.Images.Count > 0 && _ordinary.Text.Length == 0 && _draftImages.Count == 0)
            {
                _ordinary.Text = input.Text;
                _draftImages.AddRange(input.Images);
                _inputEpoch++;
                _notice = "Submission failed; text and image draft restored";
            }
            else if (input.Images.Count > 0)
            {
                _failedImageSubmission = input;
                _notice = "Failed image submission preserved; F9 to recover or discard before submitting again";
            }
        },
            cancellationToken);
    }

    private async Task AdmitClipboardImageAsync(byte[] bytes, Modal? modal, TuiKitComposer composer, long epoch, SessionId? session, CancellationToken cancellationToken)
    {
        if (_imageArtifacts is null || session is null || modal is not null || !ReferenceEquals(composer, _ordinary))
        {
            await EnqueueAsync(() => _notice = "Image paste is available only in the ordinary conversation composer; steering and secondary prompts accept text", cancellationToken);
            return;
        }

        if (_draftImages.Count >= ImageArtifactLimits.MaximumCount
            || _draftImages.Sum(image => (long)image.ByteLength) + bytes.Length > ImageArtifactLimits.MaximumRequestBytes)
        {
            await EnqueueAsync(() => _notice = "Attachment limit reached; use F9 to remove an image", cancellationToken);
            return;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var artifact = await _imageArtifacts.StoreImageAsync(stream, session.Value, "clipboard", cancellationToken: cancellationToken);
        var retained = false;
        try
        {
            await EnqueueAsync(
            () =>
            {
                if (CanEdit && !_app.Modals.IsActive && ReferenceEquals(_composer, composer)
                    && epoch == _inputEpoch && _attachmentSession == session
                    && _draftImages.Count < ImageArtifactLimits.MaximumCount
                    && _draftImages.Sum(image => (long)image.ByteLength) + artifact.ByteLength <= ImageArtifactLimits.MaximumRequestBytes)
                {
                    _draftImages.Add(artifact);
                    retained = true;
                    _notice = $"Image attached: {artifact.MediaType}, {artifact.Width}×{artifact.Height}, {artifact.ByteLength:N0} bytes — F9 inspect/remove";
                    _agents.Main.Transcript.Present(new PresentationBatch([new PresentationTextItem([
                        new($"Image attached to next message: {artifact.MediaType}, {artifact.Width}×{artifact.Height}, {artifact.ByteLength:N0} bytes\n", PresentationTextRole.Status),
                    ])]));
                }
                else
                {
                    _notice = "Image paste cancelled: input destination changed";
                }
            },
            cancellationToken);
        }
        finally
        {
            if (!retained)
            {
                await _imageArtifacts.ReleaseImageAsync(artifact.Id, session.Value, CancellationToken.None);
            }
        }
    }

    private async Task InspectAttachmentsAsync(CancellationToken cancellationToken)
    {
        if (_failedImageSubmission is { } failed)
        {
            var recovery = await SelectAsync(
                new InteractionSelectionRequest("Failed image submission", [
                new InteractionSelectionOption("restore", "Restore (requires an empty current draft)"),
                new InteractionSelectionOption("discard", "Discard failed submission; keep current draft"),
                new InteractionSelectionOption("manage", "Inspect/remove current draft attachments"),
            ]),
                cancellationToken);
            if (!recovery.IsCancelled && recovery.SelectedOptionId == "restore")
            {
                await EnqueueAsync(
                    () =>
                {
                    if (_ordinary.Text.Length == 0 && _draftImages.Count == 0)
                    {
                        _ordinary.Text = failed.Text;
                        _draftImages.AddRange(failed.Images);
                        _failedImageSubmission = null;
                        _inputEpoch++;
                    }
                    else
                    {
                        _notice = "Current draft preserved; clear it before restoring the failed submission";
                    }
                },
                    cancellationToken);
            }
            else if (!recovery.IsCancelled && recovery.SelectedOptionId == "discard")
            {
                if (_imageArtifacts is not null && _attachmentSession is { } owner)
                {
                    foreach (var image in failed.Images)
                    {
                        await _imageArtifacts.ReleaseImageAsync(image.Id, owner, cancellationToken);
                    }
                }

                await EnqueueAsync(() => _failedImageSubmission = null, cancellationToken);
            }

            if (recovery.IsCancelled || recovery.SelectedOptionId != "manage")
            {
                return;
            }
        }

        var images = _draftImages.ToArray();
        var session = _attachmentSession;
        if (session is null || _imageArtifacts is null)
        {
            await EnqueueAsync(() => _notice = "No image attachments", cancellationToken);
            return;
        }

        InteractionSelectionOption[] options =
        [
            new("paste", "Paste image from clipboard"),
            .. images.Select((image, index) => new InteractionSelectionOption(image.Id, $"Remove {index + 1}: {image.Source} · {image.MediaType} · {image.Width}×{image.Height} · {image.ByteLength:N0} bytes")),
        ];
        var selection = await SelectAsync(new InteractionSelectionRequest("Image attachments", options), cancellationToken);
        if (!selection.IsCancelled && selection.SelectedOptionId == "paste"
            && _attachmentSession == session && CurrentPurpose == ComposerPurpose.Conversation)
        {
            await PasteClipboardAsync(null, null, _ordinary, _inputEpoch, true, cancellationToken);
        }
        else if (!selection.IsCancelled && selection.SelectedOptionId is not null)
        {
            ImageArtifact? removed = null;
            await EnqueueAsync(
            () =>
            {
                if (_attachmentSession == session)
                {
                    removed = _draftImages.FirstOrDefault(image => image.Id == selection.SelectedOptionId);
                    if (removed is not null)
                    {
                        _draftImages.Remove(removed);
                        _inputEpoch++;
                        _notice = "Image removed";
                    }
                }
            },
            cancellationToken);
            if (removed is not null)
            {
                await _imageArtifacts.ReleaseImageAsync(removed.Id, session.Value, cancellationToken);
            }
        }
    }
}
