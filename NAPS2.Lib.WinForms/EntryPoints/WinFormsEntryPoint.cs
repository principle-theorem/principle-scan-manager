﻿using System.Reflection;
using System.Threading;
using Autofac;
using Eto.Forms;
using Eto.WinForms.Forms;
using NAPS2.EtoForms;
using NAPS2.EtoForms.Ui;
using NAPS2.EtoForms.WinForms;
using NAPS2.Modules;
using NAPS2.Platform.Windows;
using NAPS2.Remoting.Worker;
using NAPS2.Scan;
using wf = System.Windows.Forms;

namespace NAPS2.EntryPoints;

/// <summary>
/// The entry point logic for PrincipleScanManager.exe, the NAPS2 GUI.
/// </summary>
public static class WinFormsEntryPoint
{
    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "worker")
        {
            return WindowsWorkerEntryPoint.Run(args.Skip(1).ToArray());
        }

        // Initialize Autofac (the DI framework)
        var container = AutoFacHelper.FromModules(
            new CommonModule(), new GdiModule(), new WinFormsModule(), new RecoveryModule(), new ContextModule());

        Paths.ClearTemp();

        // Parse the command-line arguments and see if we're doing something other than displaying the main form
        var lifecycle = container.Resolve<WindowsApplicationLifecycle>();
        lifecycle.ParseArgs(args);
        lifecycle.ExitIfRedundant();

        // Start a pending worker process
        container.Resolve<IWorkerFactory>().Init(container.Resolve<ScanningContext>());

        // Set up basic application configuration
        container.Resolve<CultureHelper>().SetCulturesFromConfig();
        // TODO: Unify unhandled exception handling across platforms
        wf.Application.ThreadException += UnhandledException;
        TaskScheduler.UnobservedTaskException += UnhandledTaskException;

        // Show the main form
        var application = EtoPlatform.Current.CreateApplication();
        var formFactory = container.Resolve<IFormFactory>();
        var desktop = formFactory.Create<DesktopForm>();

        // We manually run an application rather than using eto as that lets us change the main form
        // TODO: PR for eto to handle mainform changes correctly
        application.MainForm = desktop;
        desktop.Show();
        var appContext = new wf.ApplicationContext(desktop.ToNative());
        Invoker.Current = new WinFormsInvoker(() => appContext.MainForm!);
        WinFormsDesktopForm.ApplicationContext = appContext;
        var setOptionsMethod =
            typeof(ApplicationHandler).GetMethod("SetOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        setOptionsMethod!.Invoke(application.Handler, Array.Empty<object>());
        wf.Application.Run(appContext);
        return 0;
    }

    private static void UnhandledTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.FatalException("An error occurred that caused the task to terminate.", e.Exception);
        e.SetObserved();
    }

    private static void UnhandledException(object? sender, ThreadExceptionEventArgs e)
    {
        Log.FatalException("An error occurred that caused the application to close.", e.Exception);
    }
}