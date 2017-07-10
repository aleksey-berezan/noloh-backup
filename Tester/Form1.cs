using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using NoLOH;
using System.Threading.Tasks;

namespace Tester
{
    public partial class Form1 : Form
    {
        private PerformanceCounter _pc = null;

        public Form1()
        {
            InitializeComponent();

            ThreadPool.SetMinThreads(500, 1000);

            numThreads.Value = numThreads2.Value = numThreads3.Value = Environment.ProcessorCount * 10;
            numThreads3.Value = Environment.ProcessorCount + 2;

            //if (PerformanceCounterCategory.Exists("NoLOH"))
            //    PerformanceCounterCategory.Delete("NoLOH");
            //if (!PerformanceCounterCategory.Exists("NoLOH"))
            //    PerformanceCounterCategory.Create("NoLOH", "NoLOH test statistics.", PerformanceCounterCategoryType.MultiInstance, "KB Cached in List", "The actual memory (in KB) that have been added to the list.");
            _pc = new PerformanceCounter("NoLOH", "KB Cached in List", "Tester");
            _pc.ReadOnly = false;

            HideExamplesTabs(this.tabMain.TabPages.Contains(this.tabPageExample1));
        }

        
        #region ArrayNoLOH Tab

        private volatile bool _cancelTransient = false;

        private void btnStartCreatingArrays_Click(object sender, EventArgs e)
        {
            if (radioLOHArray.Checked)
                StartCreatingArraysLOH();
            else
                StartCreatingArraysNoLOH();

            btnStartCreatingArrays.Enabled = false;
        }
        unsafe private void StartCreatingArraysLOH()
        {
            int sizeOfTransientObject = (int)numByteArraySize.Value;
            if (radioCachedIntArray.Checked) sizeOfTransientObject = (int)numIntArraySize.Value;
            else if (radioCachedLongArray.Checked) sizeOfTransientObject = (int)numLongArraySize.Value;
            Tuple<bool, bool, bool> radios = new Tuple<bool, bool, bool>(radioByteArray.Checked, radioIntArray.Checked, radioLongArray.Checked);

            _cancelTransient = false;

            for (int i = 0; i < numThreads.Value; i++)
            {
                Task.Run(() =>
                {
                    Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);

                    while (!_cancelTransient)
                    {
                        int size = rnd.Next(sizeOfTransientObject, sizeOfTransientObject + 1000);

                        if (radios.Item1) //byte
                        {
                            byte[] array = new byte[size];
                            fixed (byte* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 1), 0x1); }
                            array = null;
                        }
                        else if (radios.Item2) //int
                        {
                            int[] array = new int[size];
                            fixed (int* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 4), 0x1); }
                            array = null;
                        }
                        else if (radios.Item3) //long
                        {
                            long[] array = new long[size];
                            fixed (long* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 8), 0x1); }
                            array = null;
                        }

                        Thread.Sleep(1);
                    }
                });
            }
        }

        private void StartCreatingArraysNoLOH()
        {
            int sizeOfTransientObject = (int)numByteArraySize.Value;
            if (radioCachedIntArray.Checked) sizeOfTransientObject = (int)numIntArraySize.Value;
            else if (radioCachedLongArray.Checked) sizeOfTransientObject = (int)numLongArraySize.Value;
            Tuple<bool, bool, bool> radios = new Tuple<bool, bool, bool>(radioByteArray.Checked, radioIntArray.Checked, radioLongArray.Checked);

            _cancelTransient = false;

            for (int i = 0; i < numThreads.Value; i++)
            {
                Task.Run(() =>
                {
                    Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);

                    while (!_cancelTransient)
                    {
                        int size = rnd.Next(sizeOfTransientObject, sizeOfTransientObject + 1000);

                        if (radios.Item1) //byte
                        {
                            using (ArrayNoLOH<byte> array = new ArrayNoLOH<byte>(size))
                            {
                                Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 1), 0x1);
                            }
                        }
                        else if (radios.Item2) //int
                        {
                            using (ArrayNoLOH<int> array = new ArrayNoLOH<int>(size))
                            {
                                Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 4), 0x1);
                            }
                        }
                        else if (radios.Item3) //long
                        {
                            using (ArrayNoLOH<long> array = new ArrayNoLOH<long>(size))
                            {
                                Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 8), 0x1);
                            }
                        }

                        Thread.Sleep(1);
                    }
                });
            }
        }

        private void btnStartCreatingCachedArrays_Click(object sender, EventArgs e)
        {
            if (radioLOHCachedArray.Checked)
                StartCreatingCachedArraysLOH();
            else
                StartCreatingCachedArraysNoLOH();

            btnStartCreatingCachedArrays.Enabled = false;
        }

        private List<byte[]> _listLOHByteArray = new List<byte[]>();
        private List<int[]> _listLOHIntArray = new List<int[]>();
        private List<long[]> _listLOHLongArray = new List<long[]>();
        unsafe private void StartCreatingCachedArraysLOH()
        {
            int sizeOfCachedObject = (int)(numCachedByteArraySize.Value);
            if (radioCachedIntArray.Checked) sizeOfCachedObject = (int)(numCachedIntArraySize.Value);
            else if (radioCachedLongArray.Checked) sizeOfCachedObject = (int)(numCachedLongArraySize.Value);
            Tuple<bool, bool, bool> radios = new Tuple<bool, bool, bool>(radioCachedByteArray.Checked, radioCachedIntArray.Checked, radioCachedLongArray.Checked);

            long totalBytesToCache = (long)(numTotalBytesToCache.Value * 1024);
            int totalMinutesToRun = (int)(numTotalMinutesToRun.Value);
            bool stopTestBytes = radioStopTestBytes.Checked;

            Task.Run(() =>
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();

                Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);
                long total = 0;
                while (true)
                {
                    int size = rnd.Next(sizeOfCachedObject, sizeOfCachedObject + 1000);

                    //////////////
                    // allocate
                    //////////////
                    if (radios.Item1) //byte
                    {
                        byte[] array = new byte[size];
                        fixed (byte* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 1), 0x1); }
                        _listLOHByteArray.Add(array);
                        total += (size * 1); if (_pc != null) _pc.RawValue = (total / 1024);
                    }
                    else if (radios.Item2) //int
                    {
                        int[] array = new int[size];
                        fixed (int* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 4), 0x1); }
                        _listLOHIntArray.Add(array);
                        total += (size * 4); if (_pc != null) _pc.RawValue = (total / 1024);
                    }
                    else if (radios.Item3) //long
                    {
                        long[] array = new long[size];
                        fixed (long* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 8), 0x1); }
                        _listLOHLongArray.Add(array);
                        total += (size * 8); if (_pc != null) _pc.RawValue = (total / 1024);
                    }

                    Thread.Sleep(1);

                    //////////////
                    // update gui
                    //////////////
                    this.Invoke(new Action(() => { lblActualBytesCached.Text = (total / 1024).ToString("#,###"); lblActualBytesCached.Refresh(); }));

                    //////////////
                    // results
                    //////////////
                    if (stopTestBytes)
                    {
                        if (total >= totalBytesToCache)
                        {
                            sw.Stop();
                            _cancelTransient = true;
                            GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                            MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, true, sw.Elapsed.ToString(), "LOH_Array") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            break;
                        }
                    }
                    else //stopTestMinutes
                    {
                        if (sw.Elapsed.Minutes >= totalMinutesToRun)
                        {
                            sw.Stop();
                            _cancelTransient = true;
                            GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                            MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, true, sw.Elapsed.ToString(), "LOH_Array") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            break;
                        }
                    }
                }
            });
        }

        private List<ArrayNoLOH<byte>> _listNoLOHByteArray = new List<ArrayNoLOH<byte>>();
        private List<ArrayNoLOH<int>> _listNoLOHIntArray = new List<ArrayNoLOH<int>>();
        private List<ArrayNoLOH<long>> _listNoLOHLongArray = new List<ArrayNoLOH<long>>();
        private void StartCreatingCachedArraysNoLOH()
        {
            int sizeOfCachedObject = (int)(numCachedByteArraySize.Value);
            if (radioCachedIntArray.Checked) sizeOfCachedObject = (int)(numCachedIntArraySize.Value);
            else if (radioCachedLongArray.Checked) sizeOfCachedObject = (int)(numCachedLongArraySize.Value);
            Tuple<bool, bool, bool> radios = new Tuple<bool, bool, bool>(radioCachedByteArray.Checked, radioCachedIntArray.Checked, radioCachedLongArray.Checked);

            long totalBytesToCache = (long)(numTotalBytesToCache.Value * 1024);
            int totalMinutesToRun = (int)(numTotalMinutesToRun.Value);
            bool stopTestBytes = radioStopTestBytes.Checked;

            Task.Run(() =>
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();

                Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);
                long total = 0;
                while (true)
                {
                    int size = rnd.Next(sizeOfCachedObject, sizeOfCachedObject + 1000);

                    //////////////
                    // allocate
                    //////////////
                    if (radios.Item1) //byte
                    {
                        ArrayNoLOH<byte> array = new ArrayNoLOH<byte>(size);
                        Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 1), 0x1);
                        _listNoLOHByteArray.Add(array);
                        total += (size * 1); if (_pc != null) _pc.RawValue = (total / 1024);
                    }
                    else if (radios.Item2) //int
                    {
                        ArrayNoLOH<int> array = new ArrayNoLOH<int>(size);
                        Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 4), 0x1);
                        _listNoLOHIntArray.Add(array);
                        total += (size * 4); if (_pc != null) _pc.RawValue = (total / 1024);
                    }
                    else if (radios.Item3) //long
                    {
                        ArrayNoLOH<long> array = new ArrayNoLOH<long>(size);
                        Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 8), 0x1);
                        _listNoLOHLongArray.Add(array);
                        total += (size * 8); if (_pc != null) _pc.RawValue = (total / 1024);
                    }

                    Thread.Sleep(1);

                    //////////////
                    // update gui
                    //////////////
                    this.Invoke(new Action(() => { lblActualBytesCached.Text = (total / 1024).ToString("#,###"); lblActualBytesCached.Refresh(); }));

                    //////////////
                    // results
                    //////////////
                    if (stopTestBytes)
                    {
                        if (total >= totalBytesToCache)
                        {
                            sw.Stop();
                            _cancelTransient = true;
                            GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                            MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, false, sw.Elapsed.ToString(), "NoLOH_Array") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            break;
                        }
                    }
                    else //stopTestMinutes
                    {
                        if (sw.Elapsed.Minutes >= totalMinutesToRun)
                        {
                            sw.Stop();
                            _cancelTransient = true;
                            GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                            MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, false, sw.Elapsed.ToString(), "NoLOH_Array") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            break;
                        }
                    }
                }
            });
        }

        #endregion

        
        #region MemoryStreamNoLOH (Serialize) Tab

        private volatile bool _cancelSerializing = false;

        private void btnStartSerializing_Click(object sender, EventArgs e)
        {
            if (radioLOHSerializing.Checked)
                StartSerializingLOH();
            else
                StartSerializingNoLOH();

            btnStartSerializing.Enabled = false;
        }
        private void StartSerializingLOH()
        {
            List<Customer> items = new List<Customer>();
            for (int i = 0; i < numClassInstancesToSerialize.Value; i++)
            {
                var c = new Customer { ID = i, FirstName = "fname" + i.ToString(), LastName = "lname" + i.ToString(), AccountInfo = new Guid("11111111-1111-1111-1111-111111111111"), CreateDate = DateTime.Now };
                items.Add(c);
            }


            for (int i = 0; i < numThreads3.Value; i++)
            {
                Task.Run(() =>
                {
                    while (!_cancelSerializing)
                    {
                        //serialize
                        byte[] bytes = Serialize(items);

                        //de-serialize
                        List<Customer> result = (List<Customer>)Deserialize(bytes);

                        Thread.Sleep(1);
                    }
                });
            }
        }

        private void StartSerializingNoLOH()
        {
            List<Customer> items = new List<Customer>();
            for (int i = 0; i < numClassInstancesToSerialize.Value; i++)
            {
                var c = new Customer { ID = i, FirstName = "fname" + i.ToString(), LastName = "lname" + i.ToString(), AccountInfo = new Guid("11111111-1111-1111-1111-111111111111"), CreateDate = DateTime.Now };
                items.Add(c);
            }


            for (int i = 0; i < numThreads3.Value; i++)
            {
                Task.Run(() =>
                {
                    while (!_cancelSerializing)
                    {
                        //serialize
                        ArrayNoLOH<byte> bytes = SerializeNoLOH(items);

                        //de-serialize
                        List<Customer> result = (List<Customer>)DeserializeNoLOH(bytes);

                        Thread.Sleep(1);
                    }
                });
            }
        }

        [Serializable]
        private class Customer
        {
            public int ID { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public Guid AccountInfo { get; set; }
            public DateTime CreateDate { get; set; }
        }

        private byte[] Serialize(object items)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryFormatter binary = new BinaryFormatter();
                binary.Serialize(ms, items);  //uses LOH
                return ms.ToArray();  //uses LOH
            }
        }
        private object Deserialize(byte[] bytes)
        {
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                BinaryFormatter binary = new BinaryFormatter();
                return binary.Deserialize(ms);
            }
        }

        private ArrayNoLOH<byte> SerializeNoLOH(object items)
        {
            using (MemoryStreamNoLOH ms = new MemoryStreamNoLOH())
            {
                BinaryFormatter binary = new BinaryFormatter();
                binary.Serialize(ms, items);  //doesn't use LOH
                return ms.ToArray();  //doesn't use LOH
            }
        }
        private object DeserializeNoLOH(ArrayNoLOH<byte> bytes)
        {
            using (UnmanagedMemoryStreamEx ms = new UnmanagedMemoryStreamEx(bytes))
            {
                BinaryFormatter binary = new BinaryFormatter();
                return binary.Deserialize(ms);
            }
        }

        private MemoryStreamNoLOH SerializeNoLOHStream(object items)
        {
            MemoryStreamNoLOH ms = new MemoryStreamNoLOH();
            new BinaryFormatter().Serialize(ms, items);  //doesn't use LOH
            ms.Position = 0;
            return ms;
        }
        private object DeserializeNoLOHStream(MemoryStreamNoLOH stream)
        {
            stream.Position = 0;
            return new BinaryFormatter().Deserialize(stream);
        }


        private void btnStartCreatingCachedArrays3_Click(object sender, EventArgs e)
        {
            if (radioLOHCachedArray3.Checked)
                StartCreatingCachedArraysLOH3();
            else
                StartCreatingCachedArraysNoLOH3();

            btnStartCreatingCachedArrays3.Enabled = false;
        }

        private List<byte[]> _listLOHByteArray3 = new List<byte[]>();
        unsafe private void StartCreatingCachedArraysLOH3()
        {
            int sizeOfCachedObject = (int)numCachedByteArraySize3.Value;
            long totalBytesToCache = (long)(numTotalBytesToCache3.Value * 1024);

            Task.Run(() =>
            {
                Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);
                long total = 0;
                while (true)
                {
                    int size = rnd.Next(sizeOfCachedObject, sizeOfCachedObject + 1000);

                    //////////////
                    // allocate
                    //////////////
                    byte[] array = new byte[size];
                    fixed (byte* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 1), 0x1); }
                    _listLOHByteArray3.Add(array);
                    total += (size * 1); if (_pc != null) _pc.RawValue = (total / 1024);

                    Thread.Sleep(1);

                    //////////////
                    // update gui
                    //////////////
                    this.Invoke(new Action(() => { lblActualBytesCached3.Text = (total / 1024).ToString("#,###"); lblActualBytesCached3.Refresh(); }));

                    //////////////
                    // results
                    //////////////
                    if (total >= totalBytesToCache)
                    {
                        _cancelSerializing = true;
                        GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                        MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, true, "", "LOH_Serializing") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                }
            });
        }

        private List<ArrayNoLOH<byte>> _listNoLOHByteArray3 = new List<ArrayNoLOH<byte>>();
        private void StartCreatingCachedArraysNoLOH3()
        {
            int sizeOfCachedObject = (int)numCachedByteArraySize3.Value;
            long totalBytesToCache = (long)(numTotalBytesToCache3.Value * 1024);

            Task.Run(() =>
            {
                Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);
                long total = 0;
                while (true)
                {
                    int size = rnd.Next(sizeOfCachedObject, sizeOfCachedObject + 1000);

                    //////////////
                    // allocate
                    //////////////
                    ArrayNoLOH<byte> array = new ArrayNoLOH<byte>(size);
                    Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 1), 0x1);
                    _listNoLOHByteArray3.Add(array);
                    total += (size * 1); if (_pc != null) _pc.RawValue = (total / 1024);

                    Thread.Sleep(1);

                    //////////////
                    // update gui
                    //////////////
                    this.Invoke(new Action(() => { lblActualBytesCached3.Text = (total / 1024).ToString("#,###"); lblActualBytesCached3.Refresh(); }));

                    //////////////
                    // results
                    //////////////
                    if (total >= totalBytesToCache)
                    {
                        _cancelSerializing = true;
                        GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                        MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, false, "", "NoLOH_Serializing") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                }
            });
        }

        #endregion

        
        #region String SplitNoLOH Tab

        private volatile bool _cancelStringSplitting = false;

        private void btnStartSplittingString_Click(object sender, EventArgs e)
        {
            if (radioLOHCachedArray2.Checked)
                StartSplittingStringLOH();
            else
                StartSplittingStringNoLOH();

            btnStartSplittingString.Enabled = false;
        }
        private void StartSplittingStringLOH()
        {
            string text = BuildDelimitedString((int)numStringSplitCharacters.Value, '^');

            for (int i = 0; i < numThreads2.Value; i++)
            {
                Task.Run(() =>
                {
                    while (!_cancelStringSplitting)
                    {
                        string[] array = text.Split('^');

                            foreach (string item in array)
                            {
                                //Debug.WriteLine(item);
                            }
                            for (int x = 0; x < array.GetLength(0); x++)
                            {
                                string item = array[x];
                            }

                        array = null;

                        Thread.Sleep(1);
                    }
                });
            }
        }

        private void StartSplittingStringNoLOH()
        {
            string text = BuildDelimitedString((int)numStringSplitCharacters.Value, '^');

            for (int i = 0; i < numThreads2.Value; i++)
            {
                Task.Run(() =>
                {
                    while (!_cancelStringSplitting)
                    {
                        //using (IDisposableList<string> array = text.SplitNoLOH('^', EnumerationBehavior.DoNotDisposeAfterEnumerated))
                        using (StringArrayNoLOH array = text.SplitNoLOH('^', EnumerationBehavior.DoNotDisposeAfterEnumerated))
                        {
                            foreach (string item in array)
                            {
                                //Debug.WriteLine(item);
                            }
                            for (int x = 0; x < array.Count; x++)
                            {
                                string item = array[x];
                            }
                        }

                        Thread.Sleep(1);
                    }
                });
            }
        }

        private void btnStartCreatingCachedArrays2_Click(object sender, EventArgs e)
        {
            if (radioLOHCachedArray2.Checked)
                StartCreatingCachedArraysLOH2();
            else
                StartCreatingCachedArraysNoLOH2();

            btnStartCreatingCachedArrays2.Enabled = false;
        }

        private List<byte[]> _listLOHByteArray2 = new List<byte[]>();
        unsafe private void StartCreatingCachedArraysLOH2()
        {
            int sizeOfCachedObject = (int)numCachedByteArraySize2.Value;
            long totalBytesToCache = (long)(numTotalBytesToCache2.Value * 1024);

            Task.Run(() =>
            {
                Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);
                long total = 0;
                while (true)
                {
                    int size = rnd.Next(sizeOfCachedObject, sizeOfCachedObject + 1000);

                    //////////////
                    // allocate
                    //////////////
                    byte[] array = new byte[size];
                    fixed (byte* p = array) { Win32.FillMemory((IntPtr)p, (IntPtr)(size * 1), 0x1); }
                    _listLOHByteArray2.Add(array);
                    total += (size * 1); if (_pc != null) _pc.RawValue = (total / 1000);

                    Thread.Sleep(1);

                    //////////////
                    // update gui
                    //////////////
                    this.Invoke(new Action(() => { lblActualBytesCached2.Text = (total / 1024).ToString("#,###"); lblActualBytesCached2.Refresh(); }));

                    //////////////
                    // results
                    //////////////
                    if (total >= totalBytesToCache)
                    {
                        _cancelStringSplitting = true;
                        GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                        MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, true, "", "LOH_StringSplitting") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                }
            });
        }

        private List<ArrayNoLOH<byte>> _listNoLOHByteArray2 = new List<ArrayNoLOH<byte>>();
        private void StartCreatingCachedArraysNoLOH2()
        {
            int sizeOfCachedObject = (int)numCachedByteArraySize2.Value;
            long totalBytesToCache = (long)(numTotalBytesToCache2.Value * 1024);

            Task.Run(() =>
            {
                Random rnd = new Random(Thread.CurrentThread.ManagedThreadId);
                long total = 0;
                while (true)
                {
                    int size = rnd.Next(sizeOfCachedObject, sizeOfCachedObject + 1000);

                    //////////////
                    // allocate
                    //////////////
                    ArrayNoLOH<byte> array = new ArrayNoLOH<byte>(size);
                    Win32.FillMemory(array.AddressOf, (IntPtr)(array.Count * 1), 0x1);
                    _listNoLOHByteArray2.Add(array);
                    total += (size * 1); if (_pc != null) _pc.RawValue = (total / 1000);

                    Thread.Sleep(1);

                    //////////////
                    // update gui
                    //////////////
                    this.Invoke(new Action(() => { lblActualBytesCached2.Text = (total / 1024).ToString("#,###"); lblActualBytesCached2.Refresh(); }));

                    //////////////
                    // results
                    //////////////
                    if (total >= totalBytesToCache)
                    {
                        _cancelStringSplitting = true;
                        GC.Collect();  //do gc.collect so the .NET CLR perf counters are accurate
                        MessageBox.Show("Results text file created in application directory. [" + CreateResultsFile(total, false, "", "NoLOH_StringSplitting") + "]", "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                }
            });
        }

        #endregion

        
        #region String Split Speed Comparisons Tab

        private void btnStringSplitSpeedTest_Click(object sender, EventArgs e)
        {
            if (radioLOHStringSplitSpeedTest.Checked)
                StringSplitSpeedTestLOH();
            else if (radioNoLOHStringSplitSpeedTest.Checked)
                StringSplitSpeedTestNoLOH();
            else
                StringSplitSpeedTestLazyLoading();
        }
        private void StringSplitSpeedTestLOH()
        {
            string text = BuildDelimitedString((int)numSpeedTestCharacters.Value, '^');

            Stopwatch sw = new Stopwatch();
            sw.Start();
            int count = 0;

            //1st test
            foreach (string item in text.Split(new char[1]{'^'}, StringSplitOptions.RemoveEmptyEntries))
            {
                count++;
            }

            //2nd test
            //string item = "";
            //string[] array = text.Split('^');
            //for (int x = 0; x < array.GetLength(0); x++)
            //{
            //    item = array[x];
            //    count++;
            //}

            //3rd test
            //string[] array = text.Split('^');
            //foreach (string item in array)
            //{
            //    count++;
            //}
            //foreach (string item in array)
            //{
            //    count++;
            //}

            sw.Stop();
            MessageBox.Show("Elapsed = " + sw.Elapsed.ToString() + "   Count = " + count.ToString(), "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void StringSplitSpeedTestNoLOH()
        {
            string text = BuildDelimitedString((int)numSpeedTestCharacters.Value, '^');

            Stopwatch sw = new Stopwatch();
            sw.Start();
            int count = 0;

            //1st test
            foreach (string item in text.SplitNoLOH('^', EnumerationBehavior.DisposeAfterEnumerated))
            {
                count++;
            }

            //2nd test
            ////using (IDisposableList<string> array = text.SplitNoLOH('^'))
            ////using (IList<string> array = text.SplitNoLOH('^'))
            //using (StringArrayNoLOH array = text.SplitNoLOH('^'))
            //{
            //    string item = "";
            //    for (int x = 0; x < array.Count; x++)
            //    {
            //        item = array[x];
            //        count++;
            //    }
            //}

            //3rd test
            //using (StringArrayNoLOH array = text.SplitNoLOH('^', EnumerationBehavior.DoNotDisposeAfterEnumerated))
            //{
            //    foreach (string item in array)
            //    {
            //        count++;
            //    }
            //    foreach (string item in array)
            //    {
            //        count++;
            //    }
            //}

            sw.Stop();
            MessageBox.Show("Elapsed = " + sw.Elapsed.ToString() + "   Count = " + count.ToString(), "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void StringSplitSpeedTestLazyLoading()
        {
            string text = BuildDelimitedString((int)numSpeedTestCharacters.Value, '^');

            Stopwatch sw = new Stopwatch();
            sw.Start();
            int count = 0;

            //1st test
            foreach (string item in text.SplitLazyLoad('^'))
            {
                count++;
            }

            sw.Stop();
            MessageBox.Show("Elapsed = " + sw.Elapsed.ToString() + "   Count = " + count.ToString(), "Test Completed...", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion

        
        #region Create Test Results Text File

        [DllImport("kernel32.dll")]
        private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(out MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public int dwLength;
            public int dwMemoryLoad;
            public long ullTotalPhys;
            public long ullAvailPhys;
            public long ullTotalPageFile;
            public long ullAvailPageFile;
            public long ullTotalVirtual;
            public long ullAvailVirtual;
            public long ullAvailExtendedVirtual;
        }

        private string CreateResultsFile(long cacheTotal, bool cacheUsesLOH, string elapsed, string fileNameSuffix)
        {
            if (cacheTotal <= 0)
                throw new ArgumentException("CacheTotal must be greater than 0.", "cacheTotal");

            ///////////////////////
            //create/clear file
            ///////////////////////
            string file = @"TestResults_" + fileNameSuffix + ".txt";
            File.WriteAllText(file, "[Test Results]" + Environment.NewLine + Environment.NewLine);

            ///////////////////////
            //get memory/counter info and do calculations
            ///////////////////////
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            GlobalMemoryStatusEx(out m);

            //long totalInstalledMemory = 0;
            //GetPhysicallyInstalledSystemMemory(out totalInstalledMemory);

            Process p = Process.GetCurrentProcess();

            long lohSize = (long)(new PerformanceCounter(".NET CLR Memory", "Large Object Heap size", p.ProcessName, true)).NextValue();
            //long gen2Size = (long)(new PerformanceCounter(".NET CLR Memory", "Gen 2 heap size", p.ProcessName, true)).NextValue();
            //long gen1Size = (long)(new PerformanceCounter(".NET CLR Memory", "Gen 1 heap size", p.ProcessName, true)).NextValue();
            //long gcCommittedBytes = (long)(new PerformanceCounter(".NET CLR Memory", "# Total committed Bytes", p.ProcessName, true)).NextValue();
            //long gcReservedBytes = (long)(new PerformanceCounter(".NET CLR Memory", "# Total reserved Bytes", p.ProcessName, true)).NextValue();

            double lohNumTimesGreaterThanCached = ((double)lohSize / 1024d) / ((double)cacheTotal / 1024d);
            double workingSetNumTimesGreaterThanCached = ((double)p.WorkingSet64 / 1024d) / ((double)cacheTotal / 1024d);

            double pagingFileUsedPercent = (double)(m.ullTotalPageFile - m.ullAvailPageFile) / (double)m.ullTotalPageFile;
            pagingFileUsedPercent = Math.Round(pagingFileUsedPercent, 2, MidpointRounding.AwayFromZero);
            long totalPagingFileUsedPercent = (long)(pagingFileUsedPercent * 100d);

            double virtualMemoryUsedPercent = (double)(m.ullTotalVirtual - m.ullAvailVirtual) / (double)m.ullTotalVirtual;
            virtualMemoryUsedPercent = Math.Round(virtualMemoryUsedPercent, 2, MidpointRounding.AwayFromZero);
            long totalVirtualMemoryUsedPercent = (long)(virtualMemoryUsedPercent * 100d);

            double physicalMemoryUsedPercent = (double)(m.ullTotalPhys - m.ullAvailPhys) / (double)m.ullTotalPhys;
            physicalMemoryUsedPercent = Math.Round(physicalMemoryUsedPercent, 2, MidpointRounding.AwayFromZero);
            long totalPhysicalMemoryUsedPercent = (long)(physicalMemoryUsedPercent * 100d);

            ///////////////////////
            //output to file
            ///////////////////////
            File.AppendAllText(file, string.Format("{0,-120}{1}{2}", "Results:", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "\"Actual Bytes Cached\" " + (cacheUsesLOH ? "(in LOH)" : "(not in LOH)"), " = ", ((cacheTotal / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("{0}", Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3,-39}{4}", "LOH Size", " = ", ((lohSize / 1024).ToString("###,###,###") + " (KB)"), "   (" + lohNumTimesGreaterThanCached.ToString("#,###,##0.0") + " times the size of cached)", Environment.NewLine));
            File.AppendAllText(file, string.Format("{0}", Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3,-39}{4}", "Working Set (RAM)", " = ", ((p.WorkingSet64 / 1024).ToString("###,###,###") + " (KB)"), "   (" + workingSetNumTimesGreaterThanCached.ToString("#,###,##0.0") + " times the size of cached)", Environment.NewLine));
            if (!string.IsNullOrEmpty(elapsed))
            {
                File.AppendAllText(file, string.Format("{0}", Environment.NewLine));
                File.AppendAllText(file, string.Format("    {0,-34}{1}", "Elapsed Time:  " + elapsed, Environment.NewLine));
            }
            File.AppendAllText(file, string.Format("{0}{1}", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("{0,-120}{1}{2}", "Current memory of process:", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Committed Memory", " = ", ((p.PrivateMemorySize64 / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("{0}", Environment.NewLine));
            //File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Virtual Memory", " = ", ((p.VirtualMemorySize64 / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            //File.AppendAllText(file, string.Format("{0}", Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3,-39}{4}", "Total virtual memory", " = ", ((m.ullTotalVirtual / 1024).ToString("###,###,###.##") + " (KB)"), (m.ullTotalVirtual / 1024) >= 200000000 ? ("   (" + ((double)((int)((double)(((double)m.ullTotalVirtual / 1024d) / 1024d / 1024d / 1024d) * 100d)) / 100d).ToString() + " TB)") : "", Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Available virtual memory", " = ", ((m.ullAvailVirtual / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3,-39}{4}", "Used virtual memory", " = ", (((m.ullTotalVirtual - m.ullAvailVirtual) / 1024).ToString("###,###,###") + " (KB)"), "   (" + totalVirtualMemoryUsedPercent.ToString() + "% used)", Environment.NewLine));
            File.AppendAllText(file, string.Format("{0}{1}", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("{0,-120}{1}{2}", "Physical memory (RAM) on machine:", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Total physical memory", " = ", ((m.ullTotalPhys / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Available physical memory", " = ", ((m.ullAvailPhys / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3,-39}{4}", "Used physical memory", " = ", (((m.ullTotalPhys - m.ullAvailPhys) / 1024).ToString("###,###,###") + " (KB)"), "   (" + totalPhysicalMemoryUsedPercent.ToString() + "% used)", Environment.NewLine));
            //File.AppendAllText(file, string.Format("{0}", Environment.NewLine));
            //File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Total installed memory", " = ", ((totalInstalledMemory).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("{0}{1}", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("{0,-120}{1}{2}", "Page file on machine:", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Total page file", " = ", ((m.ullTotalPageFile / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3}", "Available page file", " = ", ((m.ullAvailPageFile / 1024).ToString("###,###,###") + " (KB)"), Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,20}{3,-39}{4}", "Used page file", " = ", (((m.ullTotalPageFile - m.ullAvailPageFile) / 1024).ToString("###,###,###") + " (KB)"), "   (" + totalPagingFileUsedPercent.ToString() + "% used)", Environment.NewLine));

            File.AppendAllText(file, string.Format("{0}{1}", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("{0,-120}{1}{2}", "GC:", Environment.NewLine, Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,15}{3}", "GC.CollectionCount(gen 0)", " = ", GC.CollectionCount(0).ToString("##0"), Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,15}{3}", "GC.CollectionCount(gen 1)", " = ", GC.CollectionCount(1).ToString("##0"), Environment.NewLine));
            File.AppendAllText(file, string.Format("    {0,-34}{1,3}{2,15}{3}", "GC.CollectionCount(gen 2)", " = ", GC.CollectionCount(2).ToString("##0"), Environment.NewLine));

            return file;
        }

        #endregion

        
        #region Build Delimited String

        private string BuildDelimitedString(int length, char delimiter)
        {
            return BuildDelimitedString(length, delimiter, false);
        }
        private string BuildDelimitedString(int length, char delimiter, bool random)
        {
            StringBuilder sb = new StringBuilder();
            Random rnd = new Random();
            int blockSize = 16, blockCount = 0, charIndex = 65;
            for (int size = 0; size < length; size++)
            {
                if (blockCount == blockSize)
                {
                    blockCount = 0;
                    if (random)
                        charIndex = rnd.Next(65, 90);  //65 = A, 90 = Z
                    else
                        charIndex = (charIndex == 90) ? 65 : ++charIndex;  //65 = A, 90 = Z
                    sb.Append(Convert.ToString(delimiter));
                }
                else
                {
                    blockCount++;
                    sb.Append(Convert.ToString(Convert.ToChar(charIndex)));
                }
            }
            return sb.ToString();
        }

        #endregion

        
        #region Event Handlers

        private void radioByteArray_CheckedChanged(object sender, EventArgs e)
        {
            numByteArraySize.Enabled = radioByteArray.Checked;
            numIntArraySize.Enabled = radioIntArray.Checked;
            numLongArraySize.Enabled = radioLongArray.Checked;
            radioCachedByteArray.Checked = radioByteArray.Checked;
        }
        private void radioIntArray_CheckedChanged(object sender, EventArgs e)
        {
            numByteArraySize.Enabled = radioByteArray.Checked;
            numIntArraySize.Enabled = radioIntArray.Checked;
            numLongArraySize.Enabled = radioLongArray.Checked;
            radioCachedIntArray.Checked = radioIntArray.Checked;
        }
        private void radioLongArray_CheckedChanged(object sender, EventArgs e)
        {
            numByteArraySize.Enabled = radioByteArray.Checked;
            numIntArraySize.Enabled = radioIntArray.Checked;
            numLongArraySize.Enabled = radioLongArray.Checked;
            radioCachedLongArray.Checked = radioLongArray.Checked;
        }

        private void radioCachedByteArray_CheckedChanged(object sender, EventArgs e)
        {
            numCachedByteArraySize.Enabled = radioCachedByteArray.Checked;
            numCachedIntArraySize.Enabled = radioCachedIntArray.Checked;
            numCachedLongArraySize.Enabled = radioCachedLongArray.Checked;
            radioByteArray.Checked = radioCachedByteArray.Checked;
        }
        private void radioCachedIntArray_CheckedChanged(object sender, EventArgs e)
        {
            numCachedByteArraySize.Enabled = radioCachedByteArray.Checked;
            numCachedIntArraySize.Enabled = radioCachedIntArray.Checked;
            numCachedLongArraySize.Enabled = radioCachedLongArray.Checked;
            radioIntArray.Checked = radioCachedIntArray.Checked;
        }
        private void radioCachedLongArray_CheckedChanged(object sender, EventArgs e)
        {
            numCachedByteArraySize.Enabled = radioCachedByteArray.Checked;
            numCachedIntArraySize.Enabled = radioCachedIntArray.Checked;
            numCachedLongArraySize.Enabled = radioCachedLongArray.Checked;
            radioLongArray.Checked = radioCachedLongArray.Checked;
        }
        private void lblShowCode_MouseEnter(object sender, EventArgs e)
        {
            ((Label)sender).ForeColor = Color.Gray;
        }
        private void lblShowCode_MouseLeave(object sender, EventArgs e)
        {
            ((Label)sender).ForeColor = Color.Yellow;
        }
        private void lblShowCode1_Click(object sender, EventArgs e)
        {
            txtCode1.Visible = !txtCode1.Visible;
        }
        private void lblShowCode2_Click(object sender, EventArgs e)
        {
            txtCode2.Visible = !txtCode2.Visible;
        }
        private void lblShowCode3_Click(object sender, EventArgs e)
        {
            txtCode3.Visible = !txtCode3.Visible;
        }
        private void lblShowCode4_Click(object sender, EventArgs e)
        {
            txtCode4.Visible = !txtCode4.Visible;
        }
        private void lblShowCode5_Click(object sender, EventArgs e)
        {
            txtCode5.Visible = !txtCode5.Visible;
        }
        private void lblShowCode6_Click(object sender, EventArgs e)
        {
            txtCode6.Visible = !txtCode6.Visible;
        }

        private void radioLOHArray_CheckedChanged(object sender, EventArgs e)
        {
            radioLOHCachedArray.Checked = radioLOHArray.Checked;
        }
        private void radioNoLOHArray_CheckedChanged(object sender, EventArgs e)
        {
            radioNoLOHCachedArray.Checked = radioNoLOHArray.Checked;
        }

        private void radioStopTestBytes_CheckedChanged(object sender, EventArgs e)
        {
            numTotalBytesToCache.Enabled = radioStopTestBytes.Checked;
            lblActualBytesCached.Enabled = radioStopTestBytes.Checked;
        }
        private void radioStopTestMinutes_CheckedChanged(object sender, EventArgs e)
        {
            numTotalMinutesToRun.Enabled = radioStopTestMinutes.Checked;
        }

        private void radioLOHSerializing_CheckedChanged(object sender, EventArgs e)
        {
            radioLOHCachedArray3.Checked = radioLOHSerializing.Checked;
        }
        private void radioNoLOHSerializing_CheckedChanged(object sender, EventArgs e)
        {
            radioNoLOHCachedArray3.Checked = radioNoLOHSerializing.Checked;
        }

        private void radioStopTestBytes3_CheckedChanged(object sender, EventArgs e)
        {
            numTotalBytesToCache3.Enabled = radioStopTestBytes3.Checked;
            lblActualBytesCached3.Enabled = radioStopTestBytes3.Checked;
        }

        private void radioLOHStringSplit_CheckedChanged(object sender, EventArgs e)
        {
            radioLOHCachedArray2.Checked = radioLOHStringSplit.Checked;
        }
        private void radioNoLOHStringSplit_CheckedChanged(object sender, EventArgs e)
        {
            radioNoLOHCachedArray2.Checked = radioNoLOHStringSplit.Checked;
        }

        private void radioStopTestBytes2_CheckedChanged(object sender, EventArgs e)
        {
            numTotalBytesToCache2.Enabled = radioStopTestBytes2.Checked;
            lblActualBytesCached2.Enabled = radioStopTestBytes2.Checked;
        }

        private void btnShowHideExamples_Click(object sender, EventArgs e)
        {
            HideExamplesTabs(this.tabMain.TabPages.Contains(this.tabPageExample1));
        }
        private void HideExamplesTabs(bool value)
        {
            if (value)
            {
                this.btnShowHideExamples.Text = "Show Examples ...";
                this.tabMain.TabPages.Remove(this.tabPageExample1);
                this.tabMain.TabPages.Remove(this.tabPageExample2);
                this.tabMain.TabPages.Remove(this.tabPageExample3);
                this.tabMain.TabPages.Remove(this.tabPageExample4);
                this.tabMain.TabPages.Remove(this.tabPageExample5);
                this.tabMain.TabPages.Remove(this.tabPageExample6);
            }
            else
            {
                this.btnShowHideExamples.Text = "Hide Examples ...";
                this.tabMain.TabPages.Add(this.tabPageExample1);
                this.tabMain.TabPages.Add(this.tabPageExample2);
                this.tabMain.TabPages.Add(this.tabPageExample3);
                this.tabMain.TabPages.Add(this.tabPageExample4);
                this.tabMain.TabPages.Add(this.tabPageExample5);
                this.tabMain.TabPages.Add(this.tabPageExample6);
            }
        }

        #endregion

    }
}