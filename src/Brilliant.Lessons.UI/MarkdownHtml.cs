namespace Brilliant.Lessons.UI;

/// <summary>Renders author-controlled markdown from the validated Content Pack. Raw HTML is disabled anyway.</summary>
internal static class MarkdownHtml
{
    private static readonly Markdig.MarkdownPipeline Pipeline =
        Markdig.MarkdownExtensions.DisableHtml(new Markdig.MarkdownPipelineBuilder()).Build();

    public static string ToHtml(string markdown) => Markdig.Markdown.ToHtml(markdown, Pipeline);
}
