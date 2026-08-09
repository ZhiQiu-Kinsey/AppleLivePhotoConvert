using LivePhotoConvert.Core.Matching;

namespace LivePhotoConvert.Core.Tests;

/// <summary>
/// 照片与视频配对的测试
/// </summary>
public class MediaPairMatcherTests
{
    /// <summary>
    /// 测试具有相同基本名称的照片和视频能被正确配对。
    /// </summary>
    [Fact]
    public void Should_Match_Photo_And_Video_With_Same_Name()
    {
        var result = MediaPairMatcher.Match([@"D:\in\IMG_0001.heic", @"D:\in\IMG_0001.mov"]);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(@"D:\in\IMG_0001.heic", pair.PhotoPath);
        Assert.Equal(@"D:\in\IMG_0001.mov", pair.VideoPath);
        Assert.Equal("IMG_0001", pair.Name);
    }

    /// <summary>
    /// 测试当同一项目存在多种照片格式（如 HEIC 和 JPG）时，会选择优先级更高的一种（HEIC）。
    /// </summary>
    [Fact]
    public void Should_Prioritize_Heic_Over_Jpg_When_Multiple_Formats_Exist()
    {
        // 从 iPhone 导出时常见 HEIC 与 JPG 并存，应该只用画质更好的 HEIC 合成一组
        var result = MediaPairMatcher.Match(
        [
            @"D:\in\IMG_0001.jpg",
            @"D:\in\IMG_0001.heic",
            @"D:\in\IMG_0001.mov"
        ]);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(@"D:\in\IMG_0001.heic", pair.PhotoPath);
        // 落选的 JPG 不参与合成，也不能被当作已匹配文件清理掉
        Assert.Equal(1, result.SkippedDuplicateCount);
    }

    /// <summary>
    /// 测试没有匹配照片的视频不会被包含在配对列表中，并被计为未匹配。
    /// </summary>
    [Fact]
    public void Unmatched_Videos_Should_Not_Be_In_Pairs_List()
    {
        var result = MediaPairMatcher.Match(
        [
            @"D:\in\IMG_0001.heic",
            @"D:\in\IMG_0001.mov",
            @"D:\in\IMG_9999.mov"
        ]);

        Assert.Single(result.Pairs);
        Assert.Equal(1, result.UnmatchedVideoCount);
        Assert.Equal(0, result.UnmatchedPhotoCount);
    }

    /// <summary>
    /// 测试没有匹配视频的照片被计为未匹配。
    /// </summary>
    [Fact]
    public void Unmatched_Photos_Should_Be_Counted()
    {
        var result = MediaPairMatcher.Match([@"D:\in\IMG_0001.heic", @"D:\in\IMG_0002.jpg"]);

        Assert.Empty(result.Pairs);
        Assert.Equal(2, result.UnmatchedPhotoCount);
    }

    /// <summary>
    /// 测试文件扩展名的大小写不影响匹配。
    /// </summary>
    [Fact]
    public void File_Extension_Case_Should_Not_Affect_Matching()
    {
        var result = MediaPairMatcher.Match([@"D:\in\IMG_0001.HEIC", @"D:\in\IMG_0001.MOV"]);

        Assert.Single(result.Pairs);
    }

    /// <summary>
    /// 测试不相关扩展名的文件（如 .txt, .aae）会被忽略，不计入任何统计。
    /// </summary>
    [Fact]
    public void Irrelevant_Files_Should_Be_Ignored()
    {
        var result = MediaPairMatcher.Match(
        [
            @"D:\in\IMG_0001.heic",
            @"D:\in\IMG_0001.mov",
            @"D:\in\readme.txt",
            @"D:\in\IMG_0001.aae"
        ]);

        Assert.Single(result.Pairs);
        Assert.Equal(0, result.UnmatchedPhotoCount);
        Assert.Equal(0, result.UnmatchedVideoCount);
        Assert.Equal(0, result.SkippedDuplicateCount);
    }

    /// <summary>
    /// 测试当同一项目存在多种视频格式（如 MP4 和 MOV）时，会选择优先级更高的一种（MOV）。
    /// </summary>
    [Fact]
    public void Should_Prioritize_Mov_Over_Mp4_When_Multiple_Formats_Exist()
    {
        var result = MediaPairMatcher.Match(
        [
            @"D:\in\IMG_0001.jpg",
            @"D:\in\IMG_0001.mp4",
            @"D:\in\IMG_0001.mov"
        ]);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(@"D:\in\IMG_0001.mov", pair.VideoPath);
        Assert.Equal(1, result.SkippedDuplicateCount);
    }

    /// <summary>
    /// 测试空列表输入应该得到空的结果。
    /// </summary>
    [Fact]
    public void Empty_List_Should_Produce_Empty_Result()
    {
        var result = MediaPairMatcher.Match([]);

        Assert.Empty(result.Pairs);
        Assert.Equal(0, result.UnmatchedPhotoCount);
        Assert.Equal(0, result.UnmatchedVideoCount);
    }
}
