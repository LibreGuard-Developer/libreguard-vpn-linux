#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;

namespace Avalonia.Controls.Android
{
    public abstract class RedirectUriReceiverActivity : Activity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            var intent = new Intent(this, typeof(AndroidAuthenticationActivity));
            _ = intent.AddFlags(ActivityFlags.ReorderToFront);
            intent.SetData(this.Intent?.Data);

            this.StartActivity(intent);

            Finish();
        }
    }
}

#endif
