using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.ComponentModel;
using Markdig;
using TacticalDisplay.App.Cloud;

namespace TacticalDisplay.App;

public partial class KneepadLibraryWindow : Window
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    public KneepadLibraryWindow(string collectionName, IReadOnlyList<KneepadPage> pages)
    {
        InitializeComponent(); Title = $"{collectionName} · Cloud Kneepad";
        var source = new CollectionViewSource { Source = pages.OrderBy(x => x.Category).ThenBy(x => x.OrderIndex).ThenBy(x => x.Title).ToList() };
        source.GroupDescriptions.Add(new PropertyGroupDescription(nameof(KneepadPage.Category)));
        Pages.ItemsSource = source.View;
        Loaded += async (_, _) => { await Viewer.EnsureCoreWebView2Async(); if (Pages.Items.Count > 0) Pages.SelectedIndex = 0; };
        Closed += (_, _) => Viewer.Dispose();
    }
    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages.SelectedItem is not KneepadPage page || Viewer.CoreWebView2 is null) return;
        var body = Markdown.ToHtml(page.ContentMarkdown, Pipeline);
        Viewer.NavigateToString($"<!doctype html><meta charset='utf-8'><style>body{{font:16px Segoe UI,sans-serif;background:#102028;color:#d9f2ec;padding:24px}}table{{border-collapse:collapse}}td,th{{border:1px solid #617480;padding:6px}}code,pre{{background:#071015}}a{{color:#9afad7}}</style><h1>{WebUtility.HtmlEncode(page.Title)}</h1>{body}");
    }
}
