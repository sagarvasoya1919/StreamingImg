using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StreamingImg
{
    public partial class Form1 : Form
    {
        private ImageStreamingServer _Server;
        public Form1()
        {
            InitializeComponent();
            this.textBox1.Text = "8084";
            this.linkLabel1.Text = string.Format("http://{0}:{1}", Environment.MachineName, this.textBox1.Text);
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            int count = (_Server != null && _Server.Clients != null ? _Server.Clients.Count() : 0);
            if (count > 0)
            {
                this.textBox1.Enabled = false;
            }
            else
            {
                this.textBox1.Enabled = true;
            }
            this.sts.Text = "Clients: " + count.ToString();
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("firefox", this.linkLabel1.Text);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if(this.button1.Text == "Start Server")
            {
                this.button1.Text = "Stop Server";

                _Server = new ImageStreamingServer();
                _Server.Start(Convert.ToInt32(this.textBox1.Text));
            }
            else
            {
                this.button1.Text = "Start Server";
                this.button1.Enabled = false;
                _Server.Dispose();
                this.button1.Enabled = true;
                this.Close();
            }
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            this.linkLabel1.Text = string.Format("http://{0}:{1}", Environment.MachineName, this.textBox1.Text);
        }
    }

    public class ImageStreamingServer : IDisposable
    {
        private List<Socket> _Clients;
        private Thread _Thread;

        public ImageStreamingServer() : this(Screen.Snapshots(600, 450, false))
        {

        }

        public ImageStreamingServer(IEnumerable<Image> imageSource)
        {
            _Clients = new List<Socket>();
            _Thread = null;

            this.ImageSource = imageSource;
            this.Interval = 50;
        }

        public IEnumerable<Image> ImageSource { get; set; }
        public int Interval { get; set; }
        public IEnumerable<Socket> Clients { get { return _Clients; } }
        public bool IsRunning { get { return (_Thread != null && _Thread.IsAlive); } }

        public void Start(int port)
        {
            lock (this)
            {
                _Thread = new Thread(new ParameterizedThreadStart(ServerThread));
                _Thread.IsBackground = true;
                _Thread.Start(port);
            }
        }

        public void Stop()
        {
            if (this.IsRunning)
            {
                try
                {
                    _Thread.Join(2000);
                    _Thread.Suspend();
                }
                finally
                {
                    lock (_Clients)
                    {
                        foreach (var s in _Clients)
                        {
                            try
                            {
                                s.Close();
                            }
                            catch
                            {
                            }
                        }
                        _Clients.Clear();
                    }
                    _Thread = null;
                }
            }
        }

        private void ServerThread(object state)
        {
            try
            {
                Socket Server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                Server.Bind(new IPEndPoint(IPAddress.Any, (int)state));
                Server.Listen(10);

                System.Diagnostics.Debug.WriteLine(string.Format("Server Started on Port {0}.", state));

                foreach (Socket client in Server.IncommingConnections())
                {
                    ThreadPool.QueueUserWorkItem(new WaitCallback(ClientThread), client);
                }
            }
            catch
            {
            }
            this.Stop();
        }

        private void ClientThread(object client)
        {
            Socket socket = (Socket)client;

            System.Diagnostics.Debug.WriteLine(string.Format("New Client From {0}", socket.RemoteEndPoint.ToString()));

            lock (_Clients)
            {
                _Clients.Add(socket);
            }

            try
            {
                using (MjpegWriter wr = new MjpegWriter(new NetworkStream(socket, true)))
                {
                    wr.WriteHeader();
                    foreach (var imgStream in Screen.Streams(this.ImageSource))
                    {
                        if (this.Interval > 0)
                        {
                            Thread.Sleep(this.Interval);
                        }
                        wr.Write(imgStream);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                lock (_Clients)
                {
                    _Clients.Remove(socket);
                }
            }
        }

        public void Dispose()
        {
            this.Stop();
        }
    }

    public class MjpegWriter : IDisposable
    {
        private static byte[] CRLF = new byte[] { 13, 10 };
        private static byte[] EmptyLine = new byte[] { 13, 10, 13, 10 };

        private string _Boundary;

        public MjpegWriter(Stream stream) : this(stream, "--boundary")
        {

        }

        public MjpegWriter(Stream stream, string boundary)
        {
            this.Stream = stream;
            this.Boundary = boundary;
        }

        public string Boundary { get; private set; }
        public Stream Stream { get; private set; }

        public void WriteHeader()
        {
            Write(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: multipart/x-mixed-replace; boundary=" +
                this.Boundary +
                "\r\n"
                );

            this.Stream.Flush();
        }

        public void Write(MemoryStream imageStream)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(this.Boundary);
            sb.AppendLine("Content-Type: image/jpeg");
            sb.AppendLine("Content-Length: " + imageStream.Length.ToString());
            sb.AppendLine();

            Write(sb.ToString());
            imageStream.WriteTo(this.Stream);
            Write("\r\n");

            this.Stream.Flush();
        }

        private void Write(string text)
        {
            byte[] data = BytesOf(text);
            this.Stream.Write(data, 0, data.Length);
        }

        private static byte[] BytesOf(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        private static MemoryStream BytesOf(Image image)
        {
            MemoryStream ms = new MemoryStream();
            image.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            return ms;
        }

        public string ReadRequest(int length)
        {
            byte[] data = new byte[length];
            int count = this.Stream.Read(data, 0, data.Length);
            if (count != 0)
                return Encoding.ASCII.GetString(data, 0, count);

            return null;
        }

        public void Dispose()
        {
            try
            {
                if (this.Stream != null)
                    this.Stream.Dispose();
            }
            finally
            {
                this.Stream = null;
            }
        }
    }

    public static class SocketExtensions
    {
        public static IEnumerable<Socket> IncommingConnections(this Socket server)
        {
            while (true)
            {
                yield return server.Accept();
            }
        }
    }

    public static class Screen
    {
        public static int _currImgIndex = 0;
        public static string _rootPath = System.Configuration.ConfigurationManager.AppSettings["JPEG_ImagesFolderPath"];
        public static readonly List<string> _imagePaths = GetAllJPEGFilePaths(_rootPath);

        public static List<string> GetAllJPEGFilePaths(string rootPath)
        {
            List<string> paths = new List<string>();
            try
            {
                string[] jpegFiles = Directory.GetFiles(rootPath, "*.jpeg", SearchOption.AllDirectories);
                if(jpegFiles != null)
                {
                    paths.AddRange(jpegFiles);
                }
            }
            catch (Exception ex)
            {
            }
            return paths;
        }

        public static void Shuffle<T>(IList<T> list)
        {
            //Fisher-Yathes Shuffle Algorithm
            Random random = new Random();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = random.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        public static IEnumerable<Image> Snapshots(int width, int height, bool showCursor)
        {
            Shuffle(_imagePaths);

            Size size = new Size(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width,
                System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);

            Bitmap srcImage = new Bitmap(size.Width, size.Height);
            Graphics srcGraphics = Graphics.FromImage(srcImage);

            bool scaled = (width != size.Width || height != size.Height);

            Bitmap dstImage = srcImage;
            Graphics dstGraphics = srcGraphics;

            if (scaled)
            {
                dstImage = new Bitmap(width, height);
                dstGraphics = Graphics.FromImage(dstImage);
            }

            Rectangle src = new Rectangle(0, 0, size.Width, size.Height);
            Rectangle dst = new Rectangle(0, 0, width, height);
            Size curSize = new Size(32, 32);

            while (true)
            {
                if (_imagePaths.Count <= 0)
                {
                    srcGraphics.CopyFromScreen(0, 0, 0, 0, size);
                }
                else
                {
                    var imagepath = _imagePaths[_currImgIndex];
                    var image = Image.FromFile(imagepath);

                    srcGraphics.DrawImage(image, 0, 0, System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width - 100,
                        System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height - 100);
                }

                if (showCursor)
                {
                    Cursors.Default.Draw(srcGraphics, new Rectangle(Cursor.Position, curSize));
                }

                if (scaled)
                {
                    dstGraphics.DrawImage(srcImage, dst, src, GraphicsUnit.Pixel);
                }

                if(_imagePaths.Count > 0)
                {
                    _currImgIndex = (_currImgIndex + 1) % _imagePaths.Count;
                }

                yield return dstImage;

                Thread.Sleep(1000);
            }

            srcGraphics.Dispose();
            dstGraphics.Dispose();

            srcImage.Dispose();
            dstImage.Dispose();

            yield break;
        }

        internal static IEnumerable<MemoryStream> Streams(this IEnumerable<Image> source)
        {
            MemoryStream ms = new MemoryStream();

            foreach (var img in source)
            {
                ms.SetLength(0);
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                yield return ms;
            }

            ms.Close();
            ms = null;

            yield break;
        }
    }
}
