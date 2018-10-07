﻿using libusbK;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FirmwareUpdater
{
    public partial class Form1 : Form
    {
        enum HUD_STATE { NotDetected=0, Application, Bootloader };
        enum HUD_COMMAND { HC_STATUS = 0x00, HC_MODE_UPD = 0x05, HC_MODE_QUERY = 0x0A, HC_MODE_SRST = 0x0F };
        enum HUD_DIR { HD_READ = 0x00, HD_WRITE = 0x01};
        enum HUD_CTRL { CB_ERROR = 0x00, CB_RESPONSE = 0x22, CB_APP_IMG = 0xCC, CB_DISP_CLR = 0xFC, CB_DISP_IMG = 0xFF };
        enum HUD_SB { SB_ERROR = 0x00, SB_TEST_FRAME = 0x0A, SB_IMGDATA = 0xCA, SB_COMMAND = 0xA5, SB_RESPONSE = 0xD5 };

        const ushort SIX15_VID = 0x2DC4;
        const ushort SIX15_PID = 0x0200;

        const ushort STM_VID = 0x0483;
        const ushort STM_PID = 0x5740;

        byte EP_CDC = 0x01;
        byte EP_DISP = 0x06;

        private KHOT_PARAMS hotInitParams;
        private HUD_STATE _currentState = HUD_STATE.NotDetected;

        private WINUSB_PIPE_INFORMATION pipeInfo;
        private UsbK usb;
        private USB_INTERFACE_DESCRIPTOR interfaceDescriptor;

        private static ushort commandId = 0;

        public Form1()
        {
            InitializeComponent();
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {

        }

        private static void dump_hex(ref byte[] byteData)
        {
            int x = 0;
            int ptr = 0;
            int length = byteData.Length;

            if (byteData.Length == 0)
            {
                Console.Write("No Data\n\n");
            }
            else
            {
                Console.Write("\nDumping {0:D} Bytes\n", length);
                for (x = 1; x <= length; x++)
                {
                    Console.Write("0x{0:X2}", byteData[ptr]);
                    if (x % 16 == 0)
                    {
                        Console.Write("\n");
                    }
                    else
                    {
                        Console.Write(" ");
                    }

                    ptr++;
                }

                Console.Write("\n\n");
            }
        }

        private static uint reverse(uint x)
        {
            x = ((x & 0x55555555) << 1) | ((x >> 1) & 0x55555555);
            x = ((x & 0x33333333) << 2) | ((x >> 2) & 0x33333333);
            x = ((x & 0x0F0F0F0F) << 4) | ((x >> 4) & 0x0F0F0F0F);
            x = (x << 24) | ((x & 0xFF00) << 8) | ((x >> 8) & 0xFF00) | (x >> 24);
            return x;
        }

        private static ushort crc16(byte[] data_p)
        {
            byte x;
            ushort crc = 0xFFFF;
            int length = data_p.Length;
            int ptr = 0;

            Console.Write("\nCalculating CRC on DATA:\n");
            dump_hex(ref data_p);

            while (length-- != 0)
            {
                x = (byte)((crc >> 8) ^ data_p[ptr]);
                ptr++;
                x ^= (byte)(x >> 4);
                crc = (byte)((crc << 8) ^ ((ushort)(x << 12)) ^ ((ushort)(x << 5)) ^ x);
            }

            Console.Write("\nCalculated CRC:0x{0:X4}\n\n", crc);
            return crc;
        }

        private static uint crc32a(byte[] message)
        {
            int i;
            int j;
            uint b;
            uint crc;
            int len = message.Length;

            i = 0;
            crc = 0xFFFFFFFF;
            while (i < len)
            {
                b = message[i]; // Get next byte.
                b = reverse(b); // 32-bit reversal.
                for (j = 0; j <= 7; j++)
                {
                    if ((int)(crc ^ b) < 0)
                    {
                        crc = (crc << 1) ^ 0x04C11DB7;
                    }
                    else
                    {
                        crc = crc << 1;
                    }
                    b = b << 1; // Ready next msg bit.
                }
                i = i + 1;
            }
            return reverse(~crc);
        }


        delegate void SetUiCallback(HUD_STATE state);

        private void setUI(HUD_STATE state)
        {
            if (this.btnReboot.InvokeRequired)
            {
                SetUiCallback d = new SetUiCallback(setUI);
                this.Invoke(d, new object[] { state });
            }
            else
            {

                _currentState = HUD_STATE.Application;

                switch (state)
                {
                    case HUD_STATE.NotDetected:
                        btnReboot.Enabled = false;
                        btnProgram.Enabled = false;
                        lblHudStatus.ForeColor = Color.Red;
                        lblHudStatus.Text = "HUD NOT CONNECTED";
                        break;
                    case HUD_STATE.Application:
                        btnReboot.Enabled = true;
                        btnProgram.Enabled = false;
                        lblHudStatus.ForeColor = Color.Green;
                        lblHudStatus.Text = "HUD CONNECTED";
                        break;
                    case HUD_STATE.Bootloader:
                        btnReboot.Enabled = false;
                        btnProgram.Enabled = true;
                        lblHudStatus.ForeColor = Color.Blue;
                        lblHudStatus.Text = "BOOT LOADER MODE";
                        break;
                    default:
                        break;
                }
            }
        }


        private void OnHotPlug(KHOT_HANDLE hotHandle,
                                      KLST_DEVINFO_HANDLE deviceInfo,
                                      KLST_SYNC_FLAG plugType)
        {
            string plugText;

            int totalPluggedDeviceCount = (int)hotHandle.GetContext().ToInt64();
            if (totalPluggedDeviceCount == int.MaxValue)
            {
                Console.WriteLine("OnHotPlug is being called for the first time on handle:{0}", hotHandle.Pointer);
                totalPluggedDeviceCount = 0;
            }

            switch (plugType)
            {
                case KLST_SYNC_FLAG.ADDED:
                    plugText = "Arrival";
                    totalPluggedDeviceCount++;
                    if (deviceInfo.DeviceID.Contains("VID_2DC4&PID_0200"))
                    {
                        setUI(HUD_STATE.Application);
                    }
                    else if (deviceInfo.DeviceID.Contains("VID_0483&PID_5740"))
                    {
                        setUI(HUD_STATE.Bootloader);
                    }
                    break;
                case KLST_SYNC_FLAG.REMOVED:
                    plugText = "Removal";
                    totalPluggedDeviceCount--;
                    if ((deviceInfo.DeviceID.Contains("VID_2DC4&PID_0200")) ||
                        (deviceInfo.DeviceID.Contains("VID_0483&PID_5740")))
                    {
                        setUI(HUD_STATE.NotDetected);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException("plugType");
            }

            hotHandle.SetContext(new IntPtr(totalPluggedDeviceCount));

            Console.WriteLine("\n[OnHotPlug] Device {0}:{1} \n",
                              plugText,
                              deviceInfo);
            Console.WriteLine("Total Plugged Device Count: {0}",
                              totalPluggedDeviceCount);
            
        }

        private int sendCommand(HUD_COMMAND cmd, HUD_DIR dir, ref byte[] buffer)
        {
            uint calcCrc = 0x00000000;
            int transferred = 0;
            ushort cmdDataLen = 0;
            bool success = false;
            int pos = 0;

            if (buffer != null)
            {
                if (buffer.Length > 492)
                {
                    tbStatus.Text += "Invalid Buffer Size mus tbe less than 492 bytes\r\n";
                    return -1;
                }
                cmdDataLen = (ushort)buffer.Length;
            }

            tbStatus.Text += "Sending data\r\n";

            /* packet header */
            byte[] temp = new byte[20 + cmdDataLen];
            temp[0] = (byte)HUD_SB.SB_COMMAND; /* start byte */
            temp[1] = (byte)HUD_CTRL.CB_DISP_IMG;  /* control flag */

            ushort len1 = (ushort)(8 + cmdDataLen);
            temp[2] = (byte)(len1 >> 8);
            temp[3] = (byte)len1;

            uint len2 = (uint)(8 + cmdDataLen);
            temp[4] = (byte)(len2 >> 24);
            temp[5] = (byte)(len2 >> 16);
            temp[6] = (byte)(len2 >> 8);
            temp[7] = (byte)len2;

            temp[8] = (byte)(calcCrc >> 24);  /* CRC */
            temp[9] = (byte)(calcCrc >> 16);
            temp[10] = (byte)(calcCrc >> 8);
            temp[11] = (byte)calcCrc;

            /* command header */
            temp[12] = (byte)cmd; /* Command Byte */
            temp[13] = (byte)dir; /* read 0x00 or write 0x01 command */
            commandId++;
            temp[14] = (byte)(commandId >> 8); /* Command ID number */
            temp[15] = (byte)commandId;

            temp[16] = (byte)(cmdDataLen >> 8); /* data length */
            temp[17] = (byte)cmdDataLen;

            temp[18] = (byte)0x00; /* reserved */
            temp[19] = (byte)0x00;

            /* command data */
            pos = 20;
            if (cmdDataLen > 0)
            {
                for (int x = 0; x < cmdDataLen; x++)
                    temp[pos] = buffer[x];
            }

            tbStatus.Text += "====> Packet Length: " + cmdDataLen + " <====\r\n";

            /* send via CDC */
            success = usb.WritePipe(EP_CDC, temp, temp.Length, out transferred, IntPtr.Zero);
            if (!success)
            {
                tbStatus.Text += "Failed to send command to HUD\r\n";
            }

            return commandId;
        }

        private int sendFrame(ref byte[] buffer, bool bCrc, HUD_CTRL ctrl)
        {
            return -1;
        }

        private void sendRebootImage(byte pipeId)  // endpoint 0x06 for HUD
        {
            bool success = true;

            byte[] sendArray = new byte[640 * 400 * 2];
            Array.Clear(sendArray, 0, sendArray.Length);
            GCHandle gch = GCHandle.Alloc(sendArray, GCHandleType.Pinned);
            int stride = 640 * 2; // 2 bytes per pixel
            Bitmap black_bmp = new Bitmap(640, 400, stride, System.Drawing.Imaging.PixelFormat.Format16bppRgb565, gch.AddrOfPinnedObject());

            using (Graphics gfx = Graphics.FromImage(black_bmp))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(0, 0, 0)))
            {
                gfx.FillRectangle(brush, 0, 0, 640, 400);
            }

            using (MemoryStream data = new MemoryStream())
            {

                black_bmp.Save(data, ImageFormat.Jpeg);
                if (data.Position <= 0x20000)
                {
                    if (data.Position > 4)
                    {
                        int transferred;
                        long orig_len = data.Position;
                        data.Position = 0;


                        success = usb.WritePipe(pipeId, data.ToArray(), data.ToArray().Length, out transferred, IntPtr.Zero);
                        Console.WriteLine("Buffer Length {0} Transferred {1} bytes.", orig_len, transferred);
                    }
                    else
                    {
                        Console.WriteLine("Not Aligned {0}", data.Position);

                    }
                }
                else
                {
                    Console.WriteLine("Too Large {0}", data.Position);
                }
            }

            usb.FlushPipe(pipeId);

        }

        private void Form1_Load(object sender, EventArgs e)
        {
            _currentState = HUD_STATE.NotDetected;

            cbFirmwareSelector.Items.Add("Darwin Proto Firmware");
            cbFirmwareSelector.Items.Add("Darwin FW 4.20");
            cbFirmwareSelector.Items.Add("Darwin FW 4.21");
            cbFirmwareSelector.Items.Add("Select Custom File");

            setUI(_currentState);

            hotInitParams = new KHOT_PARAMS();

            // In the real world, you would want to filter for only *your* device(s).
            hotInitParams.PatternMatch.DeviceInterfaceGUID = "*";

            // The PLUG_ALL_ON_INIT flag will force plug events for matching devices that are already connected.
            hotInitParams.Flags = KHOT_FLAG.PLUG_ALL_ON_INIT;

            hotInitParams.OnHotPlug = OnHotPlug;

            // You may set your initial hot handle user context like this.
            // This example is using it to count connected devices and detect the first OnHotPlug event (Int32.MaxValue).
            AllKFunctions.LibK_SetDefaultContext(KLIB_HANDLE_TYPE.HOTK, new IntPtr(Int32.MaxValue));

            // Start hot-plug detection.
            HotK hot = new HotK(ref hotInitParams);

            tbStatus.Text = "Firmware Updater v1.0\r\n";
            tbStatus.Text += "Application started\r\n";

        }
    }
}
