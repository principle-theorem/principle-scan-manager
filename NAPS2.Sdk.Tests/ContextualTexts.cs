﻿using System;
using System.Drawing;
using System.IO;
using System.Threading;
using NAPS2.Config;
using NAPS2.Images;
using NAPS2.Images.Storage;

namespace NAPS2.Sdk.Tests;

public class ContextualTexts : IDisposable
{
    public ContextualTexts()
    {
        FolderPath = $"naps2_test_temp/{Path.GetRandomFileName()}";
        Folder = Directory.CreateDirectory(FolderPath);
        var tempPath = Path.Combine(FolderPath, "temp");
        Directory.CreateDirectory(tempPath);

        ProfileManager = new StubProfileManager();
        ImageContext = new GdiImageContext
        {
            FileStorageManager = new FileStorageManager(tempPath)
        };
    }

    public IProfileManager ProfileManager { get; }

    public ImageContext ImageContext { get; }

    public string FolderPath { get; }

    public DirectoryInfo Folder { get; }

    public void UseFileStorage()
    {
        ImageContext.ConfigureBackingStorage<FileStorage>();
    }

    public void UseRecovery()
    {
        var recoveryFolderPath = Path.Combine(FolderPath, "recovery", Path.GetRandomFileName());
        ImageContext.UseRecovery(recoveryFolderPath);
    }

    public ScannedImage CreateScannedImage()
    {
        return ImageContext.CreateScannedImage(new GdiImage(new Bitmap(100, 100)));
    }

    public virtual void Dispose()
    {
        ImageContext.Dispose();
        try
        {
            Directory.Delete(FolderPath, true);
        }
        catch (IOException)
        {
            Thread.Sleep(100);
            Directory.Delete(FolderPath, true);
        }
    }
}