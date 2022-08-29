#nullable enable

using System;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using DevToys.Api.Core;
using DevToys.Api.Core.Settings;
using DevToys.Api.Tools;
using DevToys.Core;
using DevToys.Core.Threading;
using DevToys.Shared.Core.Threading;
using DevToys.Views.Tools.QRCodeEncoderDecoder;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace DevToys.ViewModels.Tools.QRCodeEncoderDecoder
{
    [Export(typeof(QRCodeEncoderDecoderToolViewModel))]
    public class QRCodeEncoderDecoderToolViewModel : ObservableRecipient, IToolViewModel, IDisposable
    {
        /// <summary>
        /// Whether the tool should encode/decode in Unicode or ASCII.
        /// </summary>
        private static readonly SettingDefinition<string> Encoder
            = new(
                name: $"{nameof(QRCodeEncoderDecoderToolViewModel)}.{nameof(Encoder)}",
                isRoaming: true,
                defaultValue: DefaultEncoding);

        private const string DefaultEncoding = "UTF-8";

        private readonly object _lockObject = new();
        private readonly List<string> _tempFileNames = new();
        private readonly IMarketingService _marketingService;

        private CancellationTokenSource? _cancellationTokenSource;
        private string? _textData;
        private StorageFile? _imageFile;
        private bool _ignoreTextDataChange;

        public Type View { get; } = typeof(QRCodeEncoderDecoderToolPage);

        internal QRCodeEncoderDecoderStrings Strings => LanguageManager.Instance.QRCodeEncoderDecoder;

        internal MockSettingsProvider MockSettingsProvider { get; }

        internal string? TextData
        {
            get => _textData;
            set
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (value != _textData)
                {
                    SetProperty(ref _textData, value);
                    if (!_ignoreTextDataChange)
                    {
                        QueueNewConversionFromTextToImage(_textData);
                    }
                }
            }
        }

        internal StorageFile? ImageFile
        {
            get => _imageFile;
            set
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (value != _imageFile)
                {
                    SetProperty(ref _imageFile, value);
                }
            }
        }

        [ImportingConstructor]
        public QRCodeEncoderDecoderToolViewModel(ISettingsProvider settingsProvider, IMarketingService marketingService)
        {
            MockSettingsProvider = new MockSettingsProvider(settingsProvider);
            _marketingService = marketingService;

            FilesSelectedCommand = new RelayCommand<StorageFile[]>(ExecuteFilesSelectedCommand);
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            ClearTempFiles();
        }

        #region FilesSelectedCommand

        public IRelayCommand<StorageFile[]> FilesSelectedCommand { get; }

        private void ExecuteFilesSelectedCommand(StorageFile[]? files)
        {
            if (files is not null)
            {
                Debug.Assert(files.Length == 1);
                QueueNewConversionFromImageToText(files[0]);
            }
        }

        #endregion

        private void QueueNewConversionFromImageToText(StorageFile file)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();

            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken cancellationToken = _cancellationTokenSource.Token;

            SetImageDataAsync(file)
                .ContinueWith(_ =>
                {
                    ConvertFromImageToTextAsync(file, cancellationToken).Forget();
                });
        }

        private void QueueNewConversionFromTextToImage(string? text)
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();

            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken cancellationToken = _cancellationTokenSource.Token;

            SetImageDataAsync(null)
                    .ContinueWith(_ =>
                    {
                        ConvertFromTextToImageAsync(text, cancellationToken).Forget();
                    });
        }


        private async Task ConvertFromTextToImageAsync(string? text, CancellationToken cancellationToken)
        {
            await TaskScheduler.Default;

            string? trimmedData = text?.Trim();

            if (string.IsNullOrWhiteSpace(trimmedData))
            {
                return;
            }

            await Task.Delay(500);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            WriteableBitmap? result = null;
            await ThreadHelper.RunOnUIThreadAsync(() =>
            {
                var barcodeWriter = new BarcodeWriter() { Format = BarcodeFormat.QR_CODE };
                var encodingOptions = new EncodingOptions()
                {
                    Width = 600,
                    Height = 600,
                    Margin = 0,
                    PureBarcode = true
                };
                encodingOptions.Hints.Add(EncodeHintType.ERROR_CORRECTION, ErrorCorrectionLevel.H);
                barcodeWriter.Options = encodingOptions;

                result = barcodeWriter.Write(trimmedData);
            });
            if (result == null)
            {
                return;
            }

            string fileType = ".png";
            StorageFolder localCacheFolder = ApplicationData.Current.LocalCacheFolder;
            StorageFile storageFile = await localCacheFolder.CreateFileAsync($"qr_code{fileType}", CreationCollisionOption.ReplaceExisting);

            _tempFileNames.Add(storageFile.Path);

            using (IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.ReadWrite))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

                await ThreadHelper.RunOnUIThreadAsync(() =>
                {
                    Stream pixelStream = result.PixelBuffer.AsStream();
                    byte[] pixels = new byte[pixelStream.Length];
                    pixelStream.Read(pixels, 0, pixels.Length);

                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)result.PixelWidth, (uint)result.PixelHeight,
                        96.0,
                        96.0,
                        pixels);

                });

                await encoder.FlushAsync();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await SetImageDataAsync(storageFile);
        }

        private async Task ConvertFromImageToTextAsync(StorageFile file, CancellationToken cancellationToken)
        {
            await TaskScheduler.Default;

            using var memStream = new MemoryStream();
            using IRandomAccessStreamWithContentType? stream = await file.OpenReadAsync();

            var barcodeReader = new BarcodeReader();
            BitmapDecoder bitmapDecoder = await BitmapDecoder.CreateAsync(stream);

            using SoftwareBitmap? softwareBitmap = await bitmapDecoder.GetSoftwareBitmapAsync();

            if (softwareBitmap == null)
            {
                return;
            }

            Result? result = barcodeReader.Decode(softwareBitmap);

            if (cancellationToken.IsCancellationRequested || result == null)
            {
                return;
            }

            await SetTextAsync(result.Text);
        }

        private async Task SetImageDataAsync(StorageFile? file)
        {
            await ThreadHelper.RunOnUIThreadAsync(() =>
            {
                ImageFile = file;
            });
        }

        private async Task SetTextAsync(string text)
        {
            await ThreadHelper.RunOnUIThreadAsync(() =>
            {
                _ignoreTextDataChange = true;
                TextData = text;
                _ignoreTextDataChange = false;
            });
        }

        private void ClearTempFiles()
        {
            for (int i = 0; i < _tempFileNames.Count; i++)
            {
                string tempFile = _tempFileNames[i];
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogFault(nameof(QRCodeEncoderDecoderToolViewModel), ex, "Unable to delete a temporary file.");
                }
            }
        }
    }
}
