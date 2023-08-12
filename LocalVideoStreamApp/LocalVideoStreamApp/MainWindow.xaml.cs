using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.IO;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace LocalVideoStreamApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 

    public partial class MainWindow : Window
    {
        private MqttClient client = null;
        const string host = "broker.mqtt-dashboard.com";
        const int port = 1883;
        const string username = "admin";
        const string password = "12345678";
        const string topic = "stream/file";

        byte[] data, data1, data2;

        public MainWindow()
        {
            InitializeComponent();
            initilizeClient();
            connectClient();
        }

        private void initilizeClient()
        {
            client = new MqttClient(host, port, false, null, null, MqttSslProtocols.None);
        }

        private void connectClient()
        {
            try
            {
                // create client instance
                string clientId = Guid.NewGuid().ToString();
                this.client.Connect(clientId, username, password);
            }
            catch (Exception ex)
            {
                Console.WriteLine("MQTT Connect error({0})", ex.Message);
                mqtt_close();
            }
        }

        private void button_Click(object sender, RoutedEventArgs e)
        {
            base.OnClosed(e);
            App.Current.Shutdown();
        }

        private void button1_Click(object sender, RoutedEventArgs e)
        {
            // Configure open file dialog box
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.FileName = "Image"; // Default file name
            dlg.DefaultExt = ".png"; // Default file extension
            dlg.Filter = "All Files (*.*)|*.*"; // Filter files by extension
            dlg.Multiselect = true;

            // Show open file dialog box
            Nullable<bool> result = dlg.ShowDialog();

            // Process open file dialog box results
            if (result == true)
            {
                // Open document
                string filePath = dlg.FileName;
                if (dlg.Multiselect)
                {
                    string[] arrAllFiles = dlg.FileNames; //used when Multiselect = true 
                }

                BitmapImage bitmapImage = new BitmapImage(new Uri(filePath)); ;
                JpegBitmapEncoder encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
                using (MemoryStream ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    data = ms.ToArray();
                }

                loadData();
                loadData1();
            }
        }

        private void loadData()
        {
            BitmapImage bitmapImage = new BitmapImage(new Uri("E:\\Work\\Screen\\2.png")); ;
            JpegBitmapEncoder encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
            using (MemoryStream ms = new MemoryStream())
            {
                encoder.Save(ms);
                data1 = ms.ToArray();
            }
        }

        private void loadData1()
        {
            BitmapImage bitmapImage = new BitmapImage(new Uri("E:\\Work\\Screen\\1.jpg")); ;
            JpegBitmapEncoder encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
            using (MemoryStream ms = new MemoryStream())
            {
                encoder.Save(ms);
                data2 = ms.ToArray();
            }
        }

        private void button2_Click(object sender, RoutedEventArgs e)
        {
            if (client == null)
                return;
            while(true)
            {
                this.client.Publish(
                    topic,
                    data,
                    MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE,
                    false
                );
                this.client.Publish(
                    topic,
                    data1,
                    MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE,
                    false
                );
                this.client.Publish(
                    topic,
                    data2,
                    MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE,
                    false
                );
            }           
        }

        private void mqtt_close()
        {
            if (this.client != null)
            {
                this.client.Disconnect();
            }
            this.client = null;

            initilizeClient();
            connectClient();
        }
    }
}
