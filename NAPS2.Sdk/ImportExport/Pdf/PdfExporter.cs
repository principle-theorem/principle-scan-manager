﻿using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NAPS2.ImportExport.Pdf.Pdfium;
using NAPS2.Ocr;
using NAPS2.Scan;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;
using PdfDocument = PdfSharp.Pdf.PdfDocument;
using PdfPage = PdfSharp.Pdf.PdfPage;

namespace NAPS2.ImportExport.Pdf;

public class PdfExporter : IPdfExporter
{
    static PdfExporter()
    {
        if (PlatformCompat.System.UseUnixFontResolver)
        {
            GlobalFontSettings.FontResolver = new UnixFontResolver();
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly ScanningContext _scanningContext;

    public PdfExporter(ScanningContext scanningContext)
    {
        _scanningContext = scanningContext;
    }

    public async Task<bool> Export(string path, ICollection<ProcessedImage> images,
        PdfExportParams exportParams, OcrParams? ocrParams = null, ProgressHandler? progressCallback = null,
        CancellationToken cancelToken = default)
    {
        return await Task.Run(async () =>
        {
            var document = InitializeDocument(exportParams);

            var ocrEngine = GetOcrEngine(ocrParams);

            var imagePages = new List<PageExportState>();
            var pdfPages = new List<PageExportState>();
            int pageIndex = 0;
            foreach (var image in images)
            {
                var pageState = new PageExportState(
                    image, pageIndex++, document, ocrEngine, ocrParams, cancelToken, exportParams.Compat);
                // TODO: To improve our ability to passthrough, we could consider using Pdfium to apply the transform to
                // the underlying PDF file. For example, doing color shifting on individual text + image objects, or
                // applying matrix changes.
                // TODO: We also can consider doing this even for scanned image transforms - e.g. for deskew, maybe
                // rather than rasterize that, rely on the pdf to do the skew transform, which should render better at
                // different scaling.
                if (IsPdfStorage(image.Storage) && image.TransformState == TransformState.Empty)
                {
                    pdfPages.Add(pageState);
                }
                else
                {
                    imagePages.Add(pageState);
                }
            }

            // TODO: Parallelize later
            // TODO: Cancellation and progress reporting
            var imagePagesPipeline = ocrEngine != null
                ? Pipeline.For(imagePages)
                    .Step(RenderStep)
                    .Step(InitOcrStep)
                    .Step(WaitForOcrStep)
                    .Step(WriteToPdfSharpStep)
                    .Run()
                : Pipeline.For(imagePages)
                    .Step(RenderStep)
                    .Step(WriteToPdfSharpStep)
                    .Run();

            var pdfPagesPrePipeline = ocrEngine != null
                ? Pipeline.For(pdfPages).Step(CheckIfOcrNeededStep).Run()
                : Task.FromResult(pdfPages);

            await pdfPagesPrePipeline;

            var pdfPagesOcrPipeline = Pipeline.For(pdfPages.Where(x => x.NeedsOcr))
                .Step(RenderStep)
                .Step(InitOcrStep)
                .Step(WaitForOcrStep)
                .Step(WriteToPdfSharpStep)
                .Run();

            await imagePagesPipeline;
            await pdfPagesOcrPipeline;

            // TODO: Doing in memory as that's presumably faster than IO, but of course that's quite a bit of memory use potentially...
            var stream = FinalizeAndSaveDocument(document, exportParams, out var placeholderPage);

            var passthroughPages = pdfPages.Where(x => !x.NeedsOcr).ToList();
            MergePassthroughPages(stream, path, passthroughPages, placeholderPage);

            return true;
        });
    }

    private void MergePassthroughPages(MemoryStream stream, string path, List<PageExportState> passthroughPages,
        bool placeholderPage)
    {
        if (!passthroughPages.Any())
        {
            if (placeholderPage)
            {
                throw new Exception("No pages to save");
            }
            using var fileStream = new FileStream(path, FileMode.Create);
            stream.CopyTo(fileStream);
            return;
        }
        lock (PdfiumNativeLibrary.Instance)
        {
            // TODO: Need to set a password if needed
            var destBuffer = stream.GetBuffer();
            var destHandle = GCHandle.Alloc(destBuffer, GCHandleType.Pinned);
            try
            {
                using var destDoc =
                    Pdfium.PdfDocument.Load(destHandle.AddrOfPinnedObject(), (int) stream.Length);
                if (placeholderPage)
                {
                    destDoc.DeletePage(0);
                }
                foreach (var state in passthroughPages)
                {
                    if (state.Image.Storage is ImageFileStorage fileStorage)
                    {
                        using var sourceDoc = Pdfium.PdfDocument.Load(fileStorage.FullPath);
                        CopyPage(destDoc, sourceDoc, state);
                    }
                    else if (state.Image.Storage is ImageMemoryStorage memoryStorage)
                    {
                        var sourceBuffer = memoryStorage.Stream.GetBuffer();
                        var sourceHandle = GCHandle.Alloc(sourceBuffer, GCHandleType.Pinned);
                        try
                        {
                            using var sourceDoc = Pdfium.PdfDocument.Load(sourceHandle.AddrOfPinnedObject(),
                                (int) memoryStorage.Stream.Length);
                            CopyPage(destDoc, sourceDoc, state);
                        }
                        finally
                        {
                            sourceHandle.Free();
                        }
                    }
                }
                destDoc.Save(path);
            }
            finally
            {
                destHandle.Free();
            }
        }
    }

    private void CopyPage(Pdfium.PdfDocument destDoc, Pdfium.PdfDocument sourceDoc, PageExportState state)
    {
        destDoc.ImportPages(sourceDoc, "1", state.PageIndex);
    }

    private PageExportState InitOcrStep(PageExportState state)
    {
        var ext = state.FileFormat == ImageFileFormat.Png ? ".png" : ".jpg";
        string ocrTempFilePath = Path.Combine(_scanningContext.TempFolderPath, Path.GetRandomFileName() + ext);
        if (!_scanningContext.OcrRequestQueue.HasCachedResult(state.OcrEngine!, state.Image, state.OcrParams!))
        {
            // Save the image to a file for use in OCR.
            // We don't need to delete this file as long as we pass it to OcrRequestQueue.Enqueue, which takes 
            // ownership and guarantees its eventual deletion.
            using var fileStream = new FileStream(ocrTempFilePath, FileMode.CreateNew);
            state.RenderedStream!.Seek(0, SeekOrigin.Begin);
            state.RenderedStream.CopyTo(fileStream);
        }

        // Start OCR
        state.OcrTask = _scanningContext.OcrRequestQueue.Enqueue(
            state.OcrEngine!, state.Image, ocrTempFilePath, state.OcrParams!, OcrPriority.Foreground,
            state.CancelToken);
        return state;
    }

    private async Task<PageExportState> WaitForOcrStep(PageExportState state)
    {
        await state.OcrTask!;
        return state;
    }

    private PageExportState WriteToPdfSharpStep(PageExportState state)
    {
        // TODO: Try and avoid locking somehow
        lock (state.Document)
        {
            using var img = XImage.FromStream(state.RenderedStream);
            // TODO: We need to serialize page adding somehow
            PdfPage page = state.Document.AddPage();
            DrawImageOnPage(page, img, state.Compat);
            // TODO: Maybe split this up to a different step
            if (state.OcrTask?.Result != null)
            {
                DrawOcrTextOnPage(page, state.OcrTask.Result);
            }
        }
        return state;
    }

    private PageExportState CheckIfOcrNeededStep(PageExportState state)
    {
        try
        {
            if (state.Image.Storage is ImageFileStorage fileStorage)
            {
                state.PageDocument = PdfReader.Open(fileStorage.FullPath, PdfDocumentOpenMode.Import);
                state.NeedsOcr = !new PdfiumPdfReader()
                    .ReadTextByPage(fileStorage.FullPath)
                    .Any(x => x.Trim().Length > 0);
            }
            else if (state.Image.Storage is ImageMemoryStorage memoryStorage)
            {
                state.PageDocument = PdfReader.Open(memoryStorage.Stream, PdfDocumentOpenMode.Import);
                state.NeedsOcr = !new PdfiumPdfReader()
                    .ReadTextByPage(memoryStorage.Stream.GetBuffer(), (int) memoryStorage.Stream.Length)
                    .Any(x => x.Trim().Length > 0);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not import PDF page for possible OCR, falling back to non-OCR path", ex);
        }
        if (!state.NeedsOcr)
        {
            // TODO: Could also switch around the checks, not sure which order is better
            state.PageDocument?.Close();
        }
        return state;
    }

    private static PdfDocument InitializeDocument(PdfExportParams exportParams)
    {
        var document = new PdfDocument();
        var creator = exportParams.Metadata.Creator;
        document.Info.Creator = string.IsNullOrEmpty(creator) ? "NAPS2" : creator;
        document.Info.Author = exportParams.Metadata.Author;
        document.Info.Keywords = exportParams.Metadata.Keywords;
        document.Info.Subject = exportParams.Metadata.Subject;
        document.Info.Title = exportParams.Metadata.Title;

        if (exportParams.Encryption?.EncryptPdf == true
            && (!string.IsNullOrEmpty(exportParams.Encryption.OwnerPassword) ||
                !string.IsNullOrEmpty(exportParams.Encryption.UserPassword)))
        {
            document.SecuritySettings.DocumentSecurityLevel = PdfDocumentSecurityLevel.Encrypted128Bit;
            if (!string.IsNullOrEmpty(exportParams.Encryption.OwnerPassword))
            {
                document.SecuritySettings.OwnerPassword = exportParams.Encryption.OwnerPassword;
            }

            if (!string.IsNullOrEmpty(exportParams.Encryption.UserPassword))
            {
                document.SecuritySettings.UserPassword = exportParams.Encryption.UserPassword;
            }

            document.SecuritySettings.PermitAccessibilityExtractContent =
                exportParams.Encryption.AllowContentCopyingForAccessibility;
            document.SecuritySettings.PermitAnnotations = exportParams.Encryption.AllowAnnotations;
            document.SecuritySettings.PermitAssembleDocument =
                exportParams.Encryption.AllowDocumentAssembly;
            document.SecuritySettings.PermitExtractContent = exportParams.Encryption.AllowContentCopying;
            document.SecuritySettings.PermitFormsFill = exportParams.Encryption.AllowFormFilling;
            document.SecuritySettings.PermitFullQualityPrint =
                exportParams.Encryption.AllowFullQualityPrinting;
            document.SecuritySettings.PermitModifyDocument =
                exportParams.Encryption.AllowDocumentModification;
            document.SecuritySettings.PermitPrint = exportParams.Encryption.AllowPrinting;
        }
        return document;
    }

    private static MemoryStream FinalizeAndSaveDocument(PdfDocument document, PdfExportParams exportParams,
        out bool placeholderPage)
    {
        placeholderPage = false;
        if (document.PageCount == 0)
        {
            document.AddPage();
            placeholderPage = true;
        }

        var compat = exportParams.Compat;
        var now = DateTime.Now;
        document.Info.CreationDate = now;
        document.Info.ModificationDate = now;
        if (compat == PdfCompat.PdfA1B)
        {
            PdfAHelper.SetCidStream(document);
            PdfAHelper.DisableTransparency(document);
        }

        if (compat != PdfCompat.Default)
        {
            PdfAHelper.SetColorProfile(document);
            PdfAHelper.SetCidMap(document);
            PdfAHelper.CreateXmpMetadata(document, compat);
        }

        var stream = new MemoryStream();
        document.Save(stream);
        return stream;
    }

    private PageExportState RenderStep(PageExportState state)
    {
        using var renderedImage = _scanningContext.ImageContext.Render(state.Image);
        var metadata = state.Image.Metadata;
        state.RenderedStream = _scanningContext.ImageContext.SaveSmallestFormatToMemoryStream(
            renderedImage, metadata.BitDepth, metadata.Lossless, -1, out var fileFormat, true);
        state.FileFormat = fileFormat;
        return state;
    }

    private IOcrEngine? GetOcrEngine(OcrParams? ocrParams)
    {
        if (ocrParams?.LanguageCode != null)
        {
            var activeEngine = _scanningContext.OcrEngine;
            if (activeEngine == null)
            {
                Log.Error("Supported OCR engine not installed.", ocrParams.LanguageCode);
            }
            else
            {
                return activeEngine;
            }
        }
        return null;
    }

    private static bool IsPdfStorage(IImageStorage storage) => storage switch
    {
        ImageFileStorage fileStorage => Path.GetExtension(fileStorage.FullPath).ToLowerInvariant() == ".pdf",
        ImageMemoryStorage memoryStorage => memoryStorage.TypeHint == ".pdf",
        _ => false
    };

    private static void DrawOcrTextOnPage(PdfPage page, OcrResult ocrResult)
    {
#if DEBUG && DEBUGOCR
            using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
#else
        using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
#endif
        var tf = new XTextFormatter(gfx);
        foreach (var element in ocrResult.Elements)
        {
            if (string.IsNullOrEmpty(element.Text)) continue;

            var adjustedBounds = AdjustBounds(element.Bounds, (float) page.Width / ocrResult.PageBounds.w,
                (float) page.Height / ocrResult.PageBounds.h);
#if DEBUG && DEBUGOCR
                    gfx.DrawRectangle(new XPen(XColor.FromArgb(255, 0, 0)), adjustedBounds);
#endif
            var adjustedFontSize = CalculateFontSize(element.Text, adjustedBounds, gfx);
            // Special case to avoid accidentally recognizing big lines as dashes/underscores
            if (adjustedFontSize > 100 && (element.Text == "-" || element.Text == "_")) continue;
            var font = new XFont("Times New Roman", adjustedFontSize, XFontStyle.Regular,
                new XPdfFontOptions(PdfFontEncoding.Unicode));
            var adjustedTextSize = gfx.MeasureString(element.Text, font);
            var verticalOffset = (adjustedBounds.Height - adjustedTextSize.Height) / 2;
            var horizontalOffset = (adjustedBounds.Width - adjustedTextSize.Width) / 2;
            adjustedBounds.Offset((float) horizontalOffset, (float) verticalOffset);
            tf.DrawString(element.RightToLeft ? ReverseText(element.Text) : element.Text, font, XBrushes.Transparent,
                adjustedBounds);
        }
    }

    private static string ReverseText(string text)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        List<string> elements = new List<string>();
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }
        elements.Reverse();
        return string.Concat(elements);
    }

    private static void DrawImageOnPage(PdfPage page, XImage img, PdfCompat compat)
    {
        if (compat != PdfCompat.Default)
        {
            img.Interpolate = false;
        }
        var (realWidth, realHeight) = GetRealSize(img);
        page.Width = realWidth;
        page.Height = realHeight;
        using XGraphics gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(img, 0, 0, realWidth, realHeight);
    }

    private static (int width, int height) GetRealSize(XImage img)
    {
        double hAdjust = 72 / img.HorizontalResolution;
        double vAdjust = 72 / img.VerticalResolution;
        if (double.IsInfinity(hAdjust) || double.IsInfinity(vAdjust))
        {
            hAdjust = vAdjust = 0.75;
        }
        double realWidth = img.PixelWidth * hAdjust;
        double realHeight = img.PixelHeight * vAdjust;
        return ((int) realWidth, (int) realHeight);
    }

    private static XRect AdjustBounds((int x, int y, int w, int h) bounds, float hAdjust, float vAdjust) =>
        new XRect(bounds.x * hAdjust, bounds.y * vAdjust, bounds.w * hAdjust, bounds.h * vAdjust);

    private static int CalculateFontSize(string text, XRect adjustedBounds, XGraphics gfx)
    {
        int fontSizeGuess = Math.Max(1, (int) (adjustedBounds.Height));
        var measuredBoundsForGuess =
            gfx.MeasureString(text, new XFont("Times New Roman", fontSizeGuess, XFontStyle.Regular));
        double adjustmentFactor = adjustedBounds.Width / measuredBoundsForGuess.Width;
        int adjustedFontSize = Math.Max(1, (int) Math.Floor(fontSizeGuess * adjustmentFactor));
        return adjustedFontSize;
    }

    private class PageExportState
    {
        public PageExportState(ProcessedImage image, int pageIndex, PdfDocument document, IOcrEngine? ocrEngine,
            OcrParams? ocrParams, CancellationToken cancelToken, PdfCompat compat)
        {
            Image = image;
            PageIndex = pageIndex;
            Document = document;
            OcrEngine = ocrEngine;
            OcrParams = ocrParams;
            CancelToken = cancelToken;
            Compat = compat;
        }

        public ProcessedImage Image { get; }
        public int PageIndex { get; }

        public PdfDocument Document { get; }
        public IOcrEngine? OcrEngine { get; }
        public OcrParams? OcrParams { get; }
        public CancellationToken CancelToken { get; }
        public PdfCompat Compat { get; }

        public bool NeedsOcr { get; set; }
        public MemoryStream? RenderedStream { get; set; }
        public ImageFileFormat FileFormat { get; set; }
        public Task<OcrResult?>? OcrTask { get; set; }
        public PdfDocument? PageDocument { get; set; }
    }

    private class UnixFontResolver : IFontResolver
    {
        private byte[]? _fontData;

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            return new FontResolverInfo(familyName, isBold, isItalic);
        }

        public byte[] GetFont(string faceName)
        {
            if (_fontData == null)
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "fc-list",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                });
                if (proc == null)
                {
                    throw new InvalidOperationException("Could not get font data from fc-list");
                }
                var fonts = proc.StandardOutput.ReadToEnd().Split('\n').Select(x => x.Split(':')[0]);
                // TODO: Maybe add more fonts here?
                var freeserif = fonts.First(f => f.EndsWith("FreeSerif.ttf", StringComparison.OrdinalIgnoreCase));
                _fontData = File.ReadAllBytes(freeserif);
            }
            return _fontData;
        }
    }
}