using Markdig;
using Microsoft.AspNetCore.Components;

namespace PmpStudy.Web.Components.Shared;

public static class Markdown
{
    // HTML brut désactivé : le Markdown est rendu sans risque d'injection.
    // Pas d'extension mathématique : les montants en « $ » ne doivent pas devenir du LaTeX.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseListExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static MarkupString Render(string? markdown) =>
        new(Markdig.Markdown.ToHtml(markdown ?? "", Pipeline));
}
