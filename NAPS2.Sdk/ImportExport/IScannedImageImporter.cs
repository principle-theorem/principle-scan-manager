﻿using System.Threading;
using NAPS2.Images;
using NAPS2.Util;

namespace NAPS2.ImportExport;

public interface IScannedImageImporter
{
    ScannedImageSource Import(string filePath, ImportParams importParams, ProgressHandler progressCallback, CancellationToken cancelToken);
}