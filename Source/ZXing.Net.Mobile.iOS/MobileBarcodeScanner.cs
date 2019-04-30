using System;
using System.Threading;
using System.Threading.Tasks;
using UIKit;

namespace ZXing.Mobile
{
    public class MobileBarcodeScanner : MobileBarcodeScannerBase
	{
		//ZxingCameraViewController viewController;
		IScannerViewController viewController;

        readonly WeakReference<UIViewController> weakAppController;
        readonly ManualResetEvent scanResultResetEvent = new ManualResetEvent(false);

		public MobileBarcodeScanner (UIViewController delegateController)
		{
			weakAppController = new WeakReference<UIViewController>(delegateController);
		}

		public MobileBarcodeScanner ()
		{
			foreach (var window in UIApplication.SharedApplication.Windows)
			{
				if (window.RootViewController != null)
				{
					weakAppController = new WeakReference<UIViewController>(window.RootViewController);
					break;
				}
			}
		}

		public Task<Result> Scan (bool useAVCaptureEngine)
		{
			return Scan (new MobileBarcodeScanningOptions (), useAVCaptureEngine);
		}


		public override Task<Result> Scan (MobileBarcodeScanningOptions options)
		{
			return Scan (options, false);
		}


        public override void ScanContinuously (MobileBarcodeScanningOptions options, Action<Result> scanHandler)
        {
            ScanContinuously (options, false, scanHandler);
        }

        public void ScanContinuously (MobileBarcodeScanningOptions options, bool useAVCaptureEngine, Action<Result> scanHandler)
        {
            try
            {
                Version.TryParse (UIDevice.CurrentDevice.SystemVersion, out var sv);

                var is7OrGreater = sv.Major >= 7;
                var allRequestedFormatsSupported = true;

                if (useAVCaptureEngine)
                    allRequestedFormatsSupported = AVCaptureScannerView.SupportsAllRequestedBarcodeFormats(options.PossibleFormats);

                if (weakAppController.TryGetTarget(out var appController))
                {
                    appController.InvokeOnMainThread(() => {

                        if (useAVCaptureEngine && is7OrGreater && allRequestedFormatsSupported)
                        {
                            viewController = new AVCaptureScannerViewController(options, this);
                            viewController.ContinuousScanning = true;
                        }
                        else
                        {
                            if (useAVCaptureEngine && !is7OrGreater)
                                Console.WriteLine("Not iOS 7 or greater, cannot use AVCapture for barcode decoding, using ZXing instead");
                            else if (useAVCaptureEngine && !allRequestedFormatsSupported)
                                Console.WriteLine("Not all requested barcode formats were supported by AVCapture, using ZXing instead");

                            viewController = new ZXingScannerViewController(options, this)
                            {
                                ContinuousScanning = true
                            };
                        }

                        viewController.OnScannedResult += barcodeResult => {

                            // If null, stop scanning was called
                            if (barcodeResult == null)
                            {
                                ((UIViewController)viewController).InvokeOnMainThread(() => {
                                    ((UIViewController)viewController).DismissViewController(true, null);
                                });
                            }

                            scanHandler(barcodeResult);
                        };

                        appController.PresentViewController((UIViewController)viewController, true, null);
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

		public Task<Result> Scan (MobileBarcodeScanningOptions options, bool useAVCaptureEngine)
		{
			return Task.Factory.StartNew(() => {

				try
				{
					scanResultResetEvent.Reset();

					Result result = null;

                    Version.TryParse (UIDevice.CurrentDevice.SystemVersion, out var sv);

					var is7OrGreater = sv.Major >= 7;
					var allRequestedFormatsSupported = true;

					if (useAVCaptureEngine)
						allRequestedFormatsSupported = AVCaptureScannerView.SupportsAllRequestedBarcodeFormats(options.PossibleFormats);

                    if (weakAppController.TryGetTarget(out var appController))
                    {
                        appController.InvokeOnMainThread(() =>
                        {


                            if (useAVCaptureEngine && is7OrGreater && allRequestedFormatsSupported)
                            {
                                viewController = new AVCaptureScannerViewController(options, this);
                            }
                            else
                            {
                                if (useAVCaptureEngine && !is7OrGreater)
                                    Console.WriteLine(
                                        "Not iOS 7 or greater, cannot use AVCapture for barcode decoding, using ZXing instead");
                                else if (useAVCaptureEngine && !allRequestedFormatsSupported)
                                    Console.WriteLine(
                                        "Not all requested barcode formats were supported by AVCapture, using ZXing instead");

                                viewController = new ZXingScannerViewController(options, this);
                            }

                            viewController.OnScannedResult += barcodeResult =>
                            {
                                ((UIViewController) viewController).InvokeOnMainThread(() =>
                                {

                                    viewController.Cancel();

                                    // Handle error situation that occurs when user manually closes scanner in the same moment that a QR code is detected
                                    try
                                    {
                                        ((UIViewController) viewController).DismissViewController(true, () =>
                                        {
                                            result = barcodeResult;
                                            scanResultResetEvent.Set();
                                        });
                                    }
                                    catch (ObjectDisposedException)
                                    {
                                        // In all likelihood, iOS has decided to close the scanner at this point. But just in case it executes the
                                        // post-scan code instead, set the result so we will not get a NullReferenceException.
                                        result = barcodeResult;
                                        scanResultResetEvent.Set();
                                    }
                                });
                            };

                            appController.PresentViewController((UIViewController) viewController, true, null);
                        });
                    }

                    scanResultResetEvent.WaitOne();
					((UIViewController)viewController).Dispose();

					return result;
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex);
					return null;
				}
			});

		}

		public override void Cancel ()
		{
            ((UIViewController) viewController)?.InvokeOnMainThread(() => {
                viewController.Cancel();

                // Calling with animated:true here will result in a blank screen when the scanner is closed on iOS 7.
                ((UIViewController)viewController).DismissViewController(false, null); 
            });

            scanResultResetEvent.Set();
		}

		public override void Torch (bool on)
        {
            viewController?.Torch (@on);
        }

		public override void ToggleTorch ()
		{
			viewController.ToggleTorch();
		}

		public override void AutoFocus ()
		{
			//Does nothing on iOS
		}

        public override void PauseAnalysis ()
        {
            viewController.PauseAnalysis ();
        }

        public override void ResumeAnalysis ()
        {
            viewController.ResumeAnalysis ();
        }

		public override bool IsTorchOn => viewController.IsTorchOn;
        public UIView CustomOverlay { get;set; }
	}
}

