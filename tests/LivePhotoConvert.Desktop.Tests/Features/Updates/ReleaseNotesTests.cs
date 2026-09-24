using LivePhotoConvert.Desktop.Features.Updates;

namespace LivePhotoConvert.Desktop.Tests.Features.Updates;

/// <summary>更新说明的 Markdown 子集：标题、列表、段落，去掉行内标记与表情符号。</summary>
public class ReleaseNotesTests
{
    [Fact]
    public void ChangelogSection_IsSplitIntoHeadingsBulletsAndParagraphs()
    {
        const string markdown = """
            ### 🩹 画廊滚动条修复
            - 修复 **拖动** 滚动条时 `Reset` 导致跳动的问题；
              续行并入上一项
            - 详见 [README](README.md)
              - 子项

            第一段第一行
            第一段第二行

            ---
            ## 3.0.2
            1. 编号项
            """;

        var blocks = ReleaseNotes.Parse(markdown);

        Assert.Equal(
            [
                new ReleaseNoteBlock(ReleaseNoteKind.Heading, "画廊滚动条修复"),
                new ReleaseNoteBlock(ReleaseNoteKind.Bullet, "修复 拖动 滚动条时 Reset 导致跳动的问题； 续行并入上一项"),
                new ReleaseNoteBlock(ReleaseNoteKind.Bullet, "详见 README"),
                new ReleaseNoteBlock(ReleaseNoteKind.Bullet, "子项", 1),
                new ReleaseNoteBlock(ReleaseNoteKind.Paragraph, "第一段第一行 第一段第二行"),
                new ReleaseNoteBlock(ReleaseNoteKind.Heading, "3.0.2"),
                new ReleaseNoteBlock(ReleaseNoteKind.Bullet, "编号项")
            ],
            blocks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n---\n")]
    public void Empty_HasNoBlocks(string? markdown) => Assert.Empty(ReleaseNotes.Parse(markdown));

    [Theory]
    [InlineData("🎉 重大重构", "重大重构")]
    [InlineData("✨ New ❤️ feature", "New feature")]
    [InlineData("a**b**c `d` [e](f) ![g](h)", "abc d e g")]
    public void Clean_RemovesMarkupAndPictographs(string input, string expected) =>
        Assert.Equal(expected, ReleaseNotes.Clean(input));

    [Fact]
    public void FencedCode_IsKeptAsPlainText()
    {
        var blocks = ReleaseNotes.Parse("```bash\ngit tag -a v3.1.0\n```");

        Assert.Equal([new ReleaseNoteBlock(ReleaseNoteKind.Paragraph, "git tag -a v3.1.0")], blocks);
    }
}
