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
    /// 测试当同一项目存在多种照片格式（如 HEIC 和 JPG）时，会生成多个候选，
    /// 且画质更好的 HEIC 排在前面（合成阶段校验通过时优先取用）。
    /// </summary>
    [Fact]
    public void Should_Generate_Candidates_Prioritizing_Heic_Over_Jpg_When_Multiple_Formats_Exist()
    {
        var result = MediaPairMatcher.Match(
        [
            @"D:\in\IMG_0001.jpg",
            @"D:\in\IMG_0001.heic",
            @"D:\in\IMG_0001.mov"
        ]);

        Assert.Equal(2, result.Pairs.Count);
        Assert.Equal(@"D:\in\IMG_0001.heic", result.Pairs[0].PhotoPath);
        Assert.Equal(@"D:\in\IMG_0001.jpg", result.Pairs[1].PhotoPath);
        Assert.All(result.Pairs, pair => Assert.Equal(@"D:\in\IMG_0001.mov", pair.VideoPath));
    }

    /// <summary>
    /// 测试同名但扩展名不同的两张照片会生成多个候选，供合成阶段按校验结果选择
    /// </summary>
    [Fact]
    public void Should_Generate_Multiple_Candidates_For_Same_Name_Different_Content()
    {
        // IMG_0435.jpg（无 ContentIdentifier 的无关照片）与 IMG_0435.jpeg（真正的实况照片）同名，
        // 两者都应作为候选保留，合成阶段再通过 ContentIdentifier 等信号选对的那张
        var result = MediaPairMatcher.Match(
        [
            @"D:\in\IMG_0435.jpg",
            @"D:\in\IMG_0435.jpeg",
            @"D:\in\IMG_0435.mov"
        ]);

        Assert.Equal(2, result.Pairs.Count);
        // .jpg 扩展名优先级高于 .jpeg，因此排在前面
        Assert.Equal(@"D:\in\IMG_0435.jpg", result.Pairs[0].PhotoPath);
        Assert.Equal(@"D:\in\IMG_0435.jpeg", result.Pairs[1].PhotoPath);
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
    }

    /// <summary>
    /// 测试当同一项目存在多种视频格式（如 MP4 和 MOV）时，会生成多个候选，且 MOV 排在前面。
    /// </summary>
    [Fact]
    public void Should_Generate_Candidates_Prioritizing_Mov_Over_Mp4_When_Multiple_Formats_Exist()
    {
        var result = MediaPairMatcher.Match(
        [
            @"D:\in\IMG_0001.jpg",
            @"D:\in\IMG_0001.mp4",
            @"D:\in\IMG_0001.mov"
        ]);

        Assert.Equal(2, result.Pairs.Count);
        Assert.Equal(@"D:\in\IMG_0001.mov", result.Pairs[0].VideoPath);
        Assert.Equal(@"D:\in\IMG_0001.mp4", result.Pairs[1].VideoPath);
        Assert.All(result.Pairs, pair => Assert.Equal(@"D:\in\IMG_0001.jpg", pair.PhotoPath));
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
