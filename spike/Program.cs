using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
static class WinRt {
  public static Task<T> AsTask<T>(this IAsyncOperation<T> op) {
    var tcs = new TaskCompletionSource<T>();
    op.Completed = (o, s) => {
      if (s == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
      else if (s == AsyncStatus.Error) tcs.TrySetException(((IAsyncInfo)o).ErrorCode);
      else tcs.TrySetCanceled();
    };
    return tcs.Task;
  }
}
class P {
  static async Task Main() {
    var inet = NetworkInformation.GetInternetConnectionProfile();
    var tm = NetworkOperatorTetheringManager.CreateFromConnectionProfile(inet);
    Console.WriteLine("state=" + tm.TetheringOperationalState);
    var r = await tm.StopTetheringAsync().AsTask();
    Console.WriteLine("stop=" + r.Status + " (expected: already off is fine)");
  }
}
