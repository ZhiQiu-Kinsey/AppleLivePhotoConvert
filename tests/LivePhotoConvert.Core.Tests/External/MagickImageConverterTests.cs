using LivePhotoConvert.Core.Abstractions;
using LivePhotoConvert.Core.External;
using LivePhotoConvert.Core.Pipeline;

namespace LivePhotoConvert.Core.Tests.External;

public class MagickImageConverterTests
{
    [Fact]
    public async Task ConvertToJpeg_DamagedSource_ThrowsImageConversionFailure()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("broken.heic", "not an image"u8.ToArray());

        var error = await Assert.ThrowsAsync<ImageConversionException>(
            () => MagickImageConverter.Instance.ConvertToJpegAsync(source, temp.Combine("out.jpg"), TestContext.Current.CancellationToken));

        Assert.Equal(OutcomeReason.ImageConversionFailed, OutcomeCause.FromException(error).Reason);
        Assert.False(File.Exists(temp.Combine("out.jpg")));
    }
}
