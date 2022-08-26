#nullable enable

using DevToys.Api.Core.Navigation;
using DevToys.Shared.Core;
using DevToys.ViewModels.Tools.QRCodeEncoderDecoder;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace DevToys.Views.Tools.QRCodeEncoderDecoder
{
    public sealed partial class QRCodeEncoderDecoderToolPage : Page
    {
        public static readonly DependencyProperty ViewModelProperty
            = DependencyProperty.Register(
                nameof(ViewModel),
                typeof(QRCodeEncoderDecoderToolViewModel),
                typeof(QRCodeEncoderDecoderToolPage),
                new PropertyMetadata(null));

        /// <summary>
        /// Gets the page's view model.
        /// </summary>
        public QRCodeEncoderDecoderToolViewModel ViewModel
        {
            get => (QRCodeEncoderDecoderToolViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public QRCodeEncoderDecoderToolPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            var parameters = (NavigationParameter)e.Parameter;

            if (ViewModel is null)
            {
                // Set the view model
                Assumes.NotNull(parameters.ViewModel, nameof(parameters.ViewModel));
                ViewModel = (QRCodeEncoderDecoderToolViewModel)parameters.ViewModel!;
                DataContext = ViewModel;
            }

            if (!string.IsNullOrWhiteSpace(parameters.ClipBoardContent))
            {
                ViewModel.Base64Data = parameters.ClipBoardContent;
            }

            base.OnNavigatedTo(e);
        }
    }
}
