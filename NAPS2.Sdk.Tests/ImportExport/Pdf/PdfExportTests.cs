using NAPS2.Images.Gdi;
using NAPS2.ImportExport.Pdf;
using NAPS2.Sdk.Tests.Asserts;
using Xunit;

namespace NAPS2.Sdk.Tests.ImportExport.Pdf;

public class PdfExporterTests : ContextualTests
{
    private readonly PdfExporter _exporter;

    public PdfExporterTests()
    {
        _exporter = new PdfExporter(ScanningContext);
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportJpegImage(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(new GdiImage(ImageResources.color_image));

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams());

        PdfAsserts.AssertImages(filePath, ImageResources.color_image);
        PdfAsserts.AssertImageFilter(filePath, 0, "DCTDecode");
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportPngImage(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(
            new GdiImage(ImageResources.color_image_png), BitDepth.Color, true, -1);

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams());

        PdfAsserts.AssertImages(filePath, ImageResources.color_image);
        PdfAsserts.AssertImageFilter(filePath, 0, "FlateDecode");
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportBlackAndWhiteImage(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(new GdiImage(ImageResources.color_image_bw));

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams());

        PdfAsserts.AssertImages(filePath, ImageResources.color_image_bw);
        PdfAsserts.AssertImageFilter(filePath, 0, "CCITTFaxDecode");
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportBlackAndWhiteImageByMetadata(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(new GdiImage(ImageResources.color_image_bw_24bit),
            BitDepth.BlackAndWhite, true, -1);

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams());

        PdfAsserts.AssertImages(filePath, ImageResources.color_image_bw);
        PdfAsserts.AssertImageFilter(filePath, 0, "CCITTFaxDecode");
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportMetadata(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(new GdiImage(ImageResources.color_image));
        var metadata = new PdfMetadata
        {
            Author = "author",
            Title = "title",
            Keywords = "keywords",
            Subject = "subject"
        };

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams { Metadata = metadata });

        PdfAsserts.AssertMetadata(metadata with { Creator = "NAPS2" }, filePath, "world");
        // TODO: We should also test embedded dates etc. somewhere, no tests for that yet 
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportUnicodeMetadata(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(new GdiImage(ImageResources.color_image));
        var metadata = new PdfMetadata
        {
            Author = "מְחַבֵּר",
            Title = "כותרת",
            Keywords = "מילות מפתח",
            Subject = "נושא"
        };

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams { Metadata = metadata });

        PdfAsserts.AssertMetadata(metadata with { Creator = "NAPS2" }, filePath, "world");
        // TODO: We should also test embedded dates etc. somewhere, no tests for that yet 
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportEncrypted(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(new GdiImage(ImageResources.color_image));

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams
        {
            Encryption = new()
            {
                EncryptPdf = true,
                OwnerPassword = "hello",
                UserPassword = "world"
            }
        });

        PdfAsserts.AssertEncrypted(filePath, "hello", "world");
    }

    [Theory]
    [ClassData(typeof(StorageAwareTestData))]
    public async Task ExportUnicodeMetadataEncrypted(StorageConfig storageConfig)
    {
        storageConfig.Apply(this);

        var filePath = Path.Combine(FolderPath, "test.pdf");
        using var image = ScanningContext.CreateProcessedImage(new GdiImage(ImageResources.color_image));
        var metadata = new PdfMetadata
        {
            Author = "מְחַבֵּר",
            Title = "כותרת",
            Keywords = "מילות מפתח",
            Subject = "נושא"
        };

        await _exporter.Export(filePath, new[] { image }, new PdfExportParams
        {
            Encryption = new()
            {
                EncryptPdf = true,
                OwnerPassword = "hello",
                UserPassword = "world"
            },
            Metadata = metadata
        });

        PdfAsserts.AssertEncrypted(filePath, "hello", "world");
        PdfAsserts.AssertMetadata(metadata with { Creator = "NAPS2" }, filePath, "world");
    }
}