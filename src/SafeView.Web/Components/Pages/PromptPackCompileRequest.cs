namespace SafeView.Web.Components.Pages;

/// <summary>
/// DTO transportowe z <c>PromptPackCompileDialog</c> do <c>PromptPacks</c> page.
/// Opakowuje user-wybory potrzebne do <see cref="SafeView.Application.Abstractions.Detection.IPromptPackCompiler.CompileAsync"/>.
/// </summary>
public sealed record PromptPackCompileRequest(
    string SourceModelId,
    IReadOnlyList<string> ClassIds,
    string? Name);
