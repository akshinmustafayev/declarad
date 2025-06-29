using System.Net.Sockets;

namespace entraform.Utils
{
    public static class TcpConnectionChecker
    {
        public static bool CheckTcpConnection(string host, int port, int timeout)
        {
            //var checker = new TcpConnectionChecker();
            //var host = "example.com"; // Replace with your host
            //var port = 5985; // Replace with your port
            //var timeout = 5000; // 5 seconds timeout
            //
            //var isConnected = checker.CheckTcpConnection(host, port, timeout);
            //Console.WriteLine($"Is connected: {isConnected}");

            using (var tcpClient = new TcpClient())
            {
                var connectResult = tcpClient.BeginConnect(host, port, null, null);
                var wait = connectResult.AsyncWaitHandle.WaitOne(timeout, false);

                if (!wait)
                {
                    tcpClient.Close();
                    return false;
                }
                else
                {
                    try
                    {
                        tcpClient.EndConnect(connectResult);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                    finally
                    {
                        tcpClient.Close();
                    }
                    return true;
                }
            }
        }
    }
}
