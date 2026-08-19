namespace Threadsmith.Validation;

using Threadsmith.Core;

/// <summary>Exposes baseline capture and mutation validation through the shared command boundary.</summary>
public sealed class ValidationApplication :
    ICommandHandler<CaptureBaselineBuildCommand, BaselineCapture>,
    ICommandHandler<ValidateMutationCommand, MutationValidationResult>
{
    private readonly BaselineBuildCapture _baselineCapture;
    private readonly IHookCoordinator? _hooks;
    private readonly ValidationPipeline _pipeline;

    /// <summary>Initializes a new instance of the <see cref="ValidationApplication"/> class.</summary>
    public ValidationApplication(
        BaselineBuildCapture baselineCapture,
        ValidationPipeline pipeline,
        IHookCoordinator? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(baselineCapture);
        ArgumentNullException.ThrowIfNull(pipeline);
        _baselineCapture = baselineCapture;
        _pipeline = pipeline;
        _hooks = hooks;
    }

    /// <inheritdoc />
    public async Task<BaselineCapture> HandleAsync(
        CaptureBaselineBuildCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);
        await InvokeBeforeAsync(command.Request, "baseline", cancellationToken);
        BaselineCapture result;
        if (RequiresBuildValidation(command.Request))
        {
            result = await _baselineCapture.CaptureAsync(command.Request, cancellationToken);
        }
        else
        {
            var mutationSet = command.MutationSet
                ?? throw new InvalidOperationException(
                    "Semantic baseline capture requires the mutation set being validated.");
            result = await _pipeline.CaptureSemanticBaselineAsync(
                command.Request,
                mutationSet,
                cancellationToken);
        }

        await InvokeAfterAsync(command.Request, "baseline", succeeded: true, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task<MutationValidationResult> HandleAsync(
        ValidateMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);
        ArgumentNullException.ThrowIfNull(command.BaselineCapture);
        ArgumentNullException.ThrowIfNull(command.MutationSet);
        await InvokeBeforeAsync(command.Request, "mutation", cancellationToken);
        var result = await _pipeline.ValidateAsync(
            command.Request,
            command.BaselineCapture,
            command.MutationSet,
            command.RequiredApprovalsPresent,
            command.FinalDiffAvailable,
            command.ResidualRisks,
            cancellationToken);
        await InvokeAfterAsync(
            command.Request,
            "mutation",
            result.Gate.Status == AcceptanceGateStatus.Passed,
            cancellationToken);
        return result;
    }

    private static bool RequiresBuildValidation(BuildValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Stages.Contains(MutationValidationStage.Compile)
            || request.Stages.Contains(MutationValidationStage.Diagnostics);
    }

    private async Task InvokeBeforeAsync(
        BuildValidationRequest request,
        string kind,
        CancellationToken cancellationToken)
    {
        if (_hooks is null)
        {
            return;
        }

        var decision = await _hooks.InvokeAsync(
            HookPoint.BeforeValidation,
            request.SessionId,
            request.RunId,
            request.Baseline.RepositoryPath,
            request.RunId.Value,
            0,
            new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["projectCount"] = request.Projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken);
        if (decision.Decision == HookDecisionKind.Block)
        {
            throw new UnauthorizedAccessException("A trusted managed lifecycle policy blocked validation.");
        }
    }

    private async Task InvokeAfterAsync(
        BuildValidationRequest request,
        string kind,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        if (_hooks is null)
        {
            return;
        }

        _ = await _hooks.InvokeAsync(
            HookPoint.AfterValidation,
            request.SessionId,
            request.RunId,
            request.Baseline.RepositoryPath,
            request.RunId.Value,
            0,
            new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["succeeded"] = succeeded.ToString(),
            },
            cancellationToken: cancellationToken);
    }
}
