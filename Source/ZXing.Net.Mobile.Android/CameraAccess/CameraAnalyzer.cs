/*
* Copyright 2018 ZXing/Redth - https://github.com/Redth/ZXing.Net.Mobile
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
*      http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
* 
* Edited by VK, Apacheta Corp 11/14/2018.
* http://www.apacheta.com/
* 
*/

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using ApxLabs.FastAndroidCamera;
using Android.Graphics;
using Android.Util;

namespace ZXing.Mobile.CameraAccess
{
    public class CameraAnalyzer
    {
        private readonly CameraController _cameraController;

        private readonly Context _context;
        private readonly MobileBarcodeScanningOptions _scanningOptions;
        private readonly CameraEventsListener _cameraEventListener;
        private int _previewHeight = -1;
        private int _previewWidth = -1;
        private (int left, int top, int width, int height) _previewArea;

        private Task _processingTask;
        private DateTime _lastPreviewAnalysis = DateTime.UtcNow;
        private bool _wasScanned;
        readonly IScannerSessionHost _scannerHost;

        
        private bool _cameraSetup;
        private GoogleVisionDetector _googleVisionDetector;

        public CameraAnalyzer(SurfaceView surfaceView, IScannerSessionHost scannerHost,
            MobileBarcodeScanningOptions scanningOptions)
        {
            _context             = surfaceView.Context;
            _scannerHost         = scannerHost;
            _scanningOptions     = scanningOptions;
            _cameraEventListener = new CameraEventsListener();
            _cameraController    = new CameraController(surfaceView, _cameraEventListener, scannerHost);
            Torch                = new Torch(_cameraController, _context);
        }

        public event EventHandler<Result> BarcodeFound;

        public Torch Torch { get; }

        public bool IsAnalyzing { get; private set; }

        public void PauseAnalysis()
        {
            IsAnalyzing = false;
        }

        public void ResumeAnalysis()
        {
            IsAnalyzing = true;
        }

        public void ShutdownCamera()
        {
            if (_cameraSetup)
            {
                IsAnalyzing =  false;
                _cameraEventListener.OnPreviewFrameReady -= HandleOnPreviewFrameReady;
                
                _cameraController.ShutdownCamera();
                _cameraSetup = false;
            }
        }

        public void SetupCamera()
        {
            if (!_cameraSetup)
            {
                _cameraEventListener.OnPreviewFrameReady += HandleOnPreviewFrameReady;
                _cameraController.SetupCamera();
                SetupLuminanceSourceArea();
                _cameraSetup = true;
            }
        }

        public void AutoFocus()
        {
            _cameraController.AutoFocus();
        }

        public void SetBestExposure(bool isTorchOn)
        {
            if (_scanningOptions.LowLightMode == true && _cameraSetup)
            {
                var cameraParameters = _cameraController.Camera.GetParameters();
                _cameraController.SetBestExposure(cameraParameters, isTorchOn);
                _cameraController.Camera.SetParameters(cameraParameters);
            }
        }

        public void AutoFocus(int x, int y)
        {
            _cameraController.AutoFocus(x, y);
        }

        public void RefreshCamera()
        {
            //only refresh the camera if it is actually setup
            if (_cameraSetup)
            {
                _cameraController.RefreshCamera();
                SetupLuminanceSourceArea();
            }  
        }

        private void SetupLuminanceSourceArea()
        {
            var cameraParameters = _cameraController.Camera.GetParameters();
            _previewWidth = cameraParameters.PreviewSize.Width;
            _previewHeight = cameraParameters.PreviewSize.Height;
        }

        private bool CanAnalyzeFrame
        {
            get
            {
                if (!IsAnalyzing)
                    return false;

                //Check and see if we're still processing a previous frame
                // todo: check if we can run as many as possible or mby run two analyzers at once (Vision + ZXing)
                if (_processingTask != null && !_processingTask.IsCompleted)
                    return false;

                var elapsedTimeMs = (DateTime.UtcNow - _lastPreviewAnalysis).TotalMilliseconds;
                if (elapsedTimeMs < _scannerHost.ScanningOptions.DelayBetweenAnalyzingFrames)
                    return false;

                // Delay a minimum between scans
                if (_wasScanned && elapsedTimeMs < _scannerHost.ScanningOptions.DelayBetweenContinuousScans)
                    return false;

                return true;
            }
        }

        private void HandleOnPreviewFrameReady(object sender, FastJavaByteArray fastArray)
        {
            if (!CanAnalyzeFrame)
                return;

            _wasScanned          = false;
            _lastPreviewAnalysis = DateTime.UtcNow;

            _processingTask = Task.Run(() =>
            {
                try
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "Preview Analyzing.");
                    DecodeFrame(fastArray);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }).ContinueWith(task =>
            {
                if (task.IsFaulted)
                    Log.Debug(MobileBarcodeScanner.TAG, "DecodeFrame exception occurs");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void DecodeFrame(FastJavaByteArray fastArray)
        { 
            Result result;

            LuminanceSource fast = new FastJavaByteArrayYUVLuminanceSource(fastArray, _previewWidth, _previewHeight,
                0,
                0,
                _previewWidth,
                _previewHeight);

            Log.Debug(MobileBarcodeScanner.TAG, "Preview width: " + _previewWidth + " preview height: " + _previewHeight);

            /* LuminanceSource fast = new FastJavaByteArrayYUVLuminanceSource(fastArray, _screenWidth, _screenHeight,
                 _previewArea.left,
                 _previewArea.top,
                 _previewArea.width,
                 _previewArea.height);*/

            // use last value for performance gain
            var cDegrees = _cameraController.LastCameraDisplayOrientationDegree;
            if (cDegrees == 90 || cDegrees == 270)
                fast = fast.rotateCounterClockwise();

            if (_scanningOptions.UseNativeScanning)
            {
                if (_googleVisionDetector == null)
                    _googleVisionDetector = new GoogleVisionDetector(_context);

                if (_googleVisionDetector.Init())
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "Reading barcode with Visio");
                    result = _googleVisionDetector.Decode(fast.Matrix, fast.Width, fast.Height);
                }
                else
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "Reading barcode with Zxing");
                    result = _scannerHost.ScanningOptions.BuildBarcodeReader().Decode(fast);
                }
            }
            else
            {
                Log.Debug(MobileBarcodeScanner.TAG, "Reading barcode with Zxing");
                result = _scannerHost.ScanningOptions.BuildBarcodeReader().Decode(fast);
            }

            fastArray.Dispose();
            fastArray = null;

            if (result != null)
            {
                Log.Debug(MobileBarcodeScanner.TAG, "Barcode Found");

                _wasScanned = true;
                BarcodeFound?.Invoke(this, result);
            }
        }
    }
}
