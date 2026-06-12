using SX3_SCANER.Helper;
using System.Windows;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Windows.Documents;
using System.Windows.Media;

namespace SX3_SCANER
{
    public partial class UpdateReleaseNotesWindow : Window
    {
        public bool Accepted { get; private set; }

        public UpdateReleaseNotesWindow(string currentVersion, UpdateInfo update)
        {
            InitializeComponent();

            txtCurrentVersion.Text = "V" + currentVersion;
            txtNewVersion.Text = "V" + update.Version;
            txtFileName.Text = update.FileName;
            txtFileSize.Text = FormatFileSize(update.FileSize);
            txtReleaseSource.Text = UpdateService.ReleasesPageUrl;

            SetReleaseNotesMarkdown(update.ReleaseNotes);

            RequiredBadge.Visibility = Visibility.Collapsed;
        }

        private void SetReleaseNotesMarkdown(string markdown)
        {
            releaseNotesDocument.Blocks.Clear();

            if (string.IsNullOrWhiteSpace(markdown))
            {
                releaseNotesDocument.Blocks.Add(new Paragraph(new Run("Không có nội dung thay đổi.")));
                return;
            }

            MarkdownDocument document = Markdown.Parse(markdown);

            foreach (Markdig.Syntax.Block block in document)
            {
                if (block is HeadingBlock heading)
                {
                    Paragraph paragraph = new Paragraph
                    {
                        Margin = new Thickness(0, heading.Level == 1 ? 0 : 14, 0, 8),
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A")),
                        FontSize = heading.Level == 1 ? 24 : heading.Level == 2 ? 19 : 16
                    };

                    AddInlineContent(paragraph.Inlines, heading.Inline);
                    releaseNotesDocument.Blocks.Add(paragraph);
                    continue;
                }

                if (block is ParagraphBlock paragraphBlock)
                {
                    Paragraph paragraph = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 8),
                        LineHeight = 25
                    };

                    AddInlineContent(paragraph.Inlines, paragraphBlock.Inline);
                    releaseNotesDocument.Blocks.Add(paragraph);
                    continue;
                }

                if (block is ListBlock listBlock)
                {
                    List list = new List
                    {
                        MarkerStyle = listBlock.IsOrdered
                            ? TextMarkerStyle.Decimal
                            : TextMarkerStyle.Disc,
                        Margin = new Thickness(18, 0, 0, 10),
                        Padding = new Thickness(8, 0, 0, 0)
                    };

                    foreach (ListItemBlock itemBlock in listBlock)
                    {
                        ListItem item = new ListItem();

                        foreach (Markdig.Syntax.Block itemChild in itemBlock)
                        {
                            if (itemChild is ParagraphBlock itemParagraphBlock)
                            {
                                Paragraph itemParagraph = new Paragraph
                                {
                                    Margin = new Thickness(0, 0, 0, 4),
                                    LineHeight = 24
                                };

                                AddInlineContent(itemParagraph.Inlines, itemParagraphBlock.Inline);
                                item.Blocks.Add(itemParagraph);
                            }
                        }

                        list.ListItems.Add(item);
                    }

                    releaseNotesDocument.Blocks.Add(list);
                    continue;
                }

                if (block is ThematicBreakBlock)
                {
                    Paragraph separator = new Paragraph
                    {
                        Margin = new Thickness(0, 10, 0, 10)
                    };
                    separator.Inlines.Add(new Run("────────────────────────")
                    {
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"))
                    });
                    releaseNotesDocument.Blocks.Add(separator);
                }
            }
        }

        private void AddInlineContent(InlineCollection inlines, ContainerInline container)
        {
            if (container == null)
            {
                return;
            }

            foreach (Markdig.Syntax.Inlines.Inline inline in container)
            {
                if (inline is LiteralInline literal)
                {
                    inlines.Add(new Run(literal.Content.ToString()));
                    continue;
                }

                if (inline is EmphasisInline emphasis)
                {
                    Span span = new Span();

                    if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeights.Bold;
                    }
                    else
                    {
                        span.FontStyle = FontStyles.Italic;
                    }

                    AddInlineContent(span.Inlines, emphasis);
                    inlines.Add(span);
                    continue;
                }

                if (inline is LineBreakInline)
                {
                    inlines.Add(new LineBreak());
                    continue;
                }

                if (inline is CodeInline code)
                {
                    Run run = new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9")),
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BE123C"))
                    };
                    inlines.Add(run);
                    continue;
                }

                if (inline is LinkInline link)
                {
                    Hyperlink hyperlink = new Hyperlink
                    {
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"))
                    };

                    AddInlineContent(hyperlink.Inlines, link);
                    inlines.Add(hyperlink);
                }
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "Không xác định";
            }

            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return size.ToString(unitIndex == 0 ? "0" : "0.##") + " " + units[unitIndex];
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            Accepted = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Accepted = false;
            DialogResult = false;
            Close();
        }
    }
}
