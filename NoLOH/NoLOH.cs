using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Collections;
using System.ComponentModel;
using System.IO;


//////////////////////////////////////////////////////////////////////////////////////////////
// This dll is designed to provide support for large objects that do not use the
// GC Large Object Heap. It provides several classes for working with large objects
// that can be used in place of the regular existing .NET CLR types that use the
// GC Large Object Heap. These classes are meant to be examples illustrating a
// simple technique for keeping large objects off the GC Large Object Heap. The
// technique uses a small disposable regular .NET CLR object that allocates
// large unmanaged memory in the objects constructor, and releases the unmanaged memory
// in the objects Dispose(). That way to the CLR it's really just a small regular
// .NET CLR object that's most likely going to be destroyed in gen0 in the GC Small
// Object Heap, thereby releasing all the large unmanaged memory and compacting the
// Small Object Heap. Compacting keeps the GC Small Object Heap from fragmenting,
// and simply keeping the object off the GC Large Object Heap altogether pretty much makes
// it impossible to fragment the GC Large Object Heap. Additionally, the unmanaged memory
// is not allocated from any heap, we go directly to the Virtual Memory Manager of the OS
// with VirtualAlloc. This avoids fragmenting any other unmanaged heap. Most heap
// implementations will also do the same thing (go directly to OS with VirtualAlloc) once an
// allocation exceeds a certain size and is very large (like over 512KB (524,288 bytes) for example).
// The classes in this dll are ONLY meant to be used for LARGE objects (over 85,000 bytes),
// so bypassing any heap in this case could possibly be thought of as somewhat normal.
// This dll does not provide support for all the different operations dealing with large objects,
// but is meant to be a good base with which to start and extend on going forward. Additionally, it
// serves as a sample of how to implement this simple technique for enhancements in the future . It
// currently supports several of the common cases that can sometimes cause big problems on production
// server's in enterprise environments when dealing with very large objects.
//
// 
// ArrayNoLOH<T>:  Can be used in place of regular value-type arrays (i.e. byte[], int[], long[])
//                 for very large objects. Right now it only supports value-type arrays for arrays
//                 of types like byte, int, long, etc. No support for reference-type arrays.
//
// MemoryStreamNoLOH:  Can be used in place of MemoryStream for a very large stream of bytes.
//
// string.SplitNoLOH('^'):  Can be used in place of string.Split('^') for very large strings that
//                          are being split. The current built-in string.Split() implementation
//                          in .NET CLR internally utilizes an int[] set to the length of the string
//                          to keep track of the split delimiter locations within the string. This
//                          int[] ends up going on the Large Object Heap when the string is roughly
//                          21,250 (since the .NET int is 4 bytes), and can sometimes cause serious
//                          LOH problems when splitting very large strings. Moreover, the string
//                          itself goes on the LOH when its over roughly 42,500 characters (since
//                          the .NET char is 2 bytes for unicode).
//
// StringArrayNoLOH:  Is not meant to be instantiated as a stand-alone instance. It's merely used
//                    as return type of SplitNoLOH (i.e. StringArrayNoLOH array = string.SplitNoLOH('^')),
//                    in place of return type of string[].
//
//
//
// How to look at Large Object Heap (GC):
//
//   WinDbg:
//
//     Open WinDbg and attach to Tester.exe process.
//
//     In WinDbg (to see fragmentation in LOH):
//       .loadby sos clr
//       !heapstat               (shows statistics for LOH, including fragmentation)
//
//     In WinDbg (to see detailed breakdown of LOH):
//       .loadby sos clr
//       !eeheap -gc
//       !dumpheap 0x????????????????    (this address is the starting address for
//                                        the LOH shown in output of !eeheap -gc)
//
//     In WinDbg (to see if heap has errors):
//       .loadby sos clr
//       !verifyheap             (verify will only produce output if there are errors in heap)
//
//
//   Visual Studio 2015:
//
//     //TODO: add how-to here...
//
//
//   Visual Studio 2010:
//
//     In Project \ Properties \ Debug:
//       Enable unmanaged code debugging
//
//     In Immediate Window (to see fragmentation in LOH):
//       .load sos
//       !heapstat               (shows statistics for LOH, including fragmentation)
//
//     In Immediate Window (to see detailed breakdown of LOH):
//       .load sos
//       !eeheap -gc
//       !dumpheap 0x????????????????    (this address is the starting address for
//                                         the LOH shown in output of !eeheap -gc)
//
//     In Immediate Window (to see if heap has errors):
//       .load sos
//       !verifyheap             (verify will only produce output if there are errors in heap)
//
//
// How to change GC mode in app.config:
//
//   <configuration>
//     <runtime>
//       <gcServer enabled = "true" />  //true=server mode, false=workstation mode
//       <gcConcurrent enabled="true"/>  //true=concurrent mode, false=not concurrent mode
//     </runtime>
//   </configuration>
//
//////////////////////////////////////////////////////////////////////////////////////////////

namespace NoLOH
{
    #region Interfaces and Enumerations

    /// <summary>
    /// For each new value-type you want to support for T for ArrayNoLOH(T) class,
    /// simply add a new IIndexer(value-type) to class declaration and implement it.
    /// See ArrayNoLOH(T) class for various value-type implementation examples.
    /// </summary>
    internal interface IIndexer<T>
    {
        T this[int index] { get; set; }
    }

    /// <summary>
    /// This just makes an IList(T) disposable, so can use in using block.
    /// </summary>
    public interface IDisposableList<T> : IList<T>, IDisposable
    {
    }

    public enum EnumerationBehavior
    {
        DisposeAfterEnumerated,
        DoNotDisposeAfterEnumerated
    }

    #endregion

    
    #region Win32

    public static class Win32
    {
        //////////////////////////////////////////////////////////////////////////////////
        // Note:  IntPtr will automatically magically adjust to int (4 bytes) for 32-bit,
        //        and long (8 bytes) for 64-bit.
        //////////////////////////////////////////////////////////////////////////////////

        // dwSize - basically size will be int (for 32-bit) and long (for 64-bit)
        // return value - basically will be 4 byte pointer address (for 32-bit)
        //                and 8 byte pointer address (for 64-bit)
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAlloc(IntPtr lpAddress, IntPtr dwSize, int flAllocationType, int flProtect);

        public const int MEM_COMMIT = 0x1000;
        public const int MEM_RESERVE = 0x2000;

        public const int PAGE_NOACCESS = 0x01;
        public const int PAGE_READONLY = 0x02;
        public const int PAGE_READWRITE = 0x04;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualFree(IntPtr lpAddress, IntPtr dwSize, int dwFreeType);

        public const int MEM_DECOMMIT = 0x4000;
        public const int MEM_RELEASE = 0x8000;

        
        // length - basically length will be int (for 32-bit) and long (for 64-bit)
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        public static extern void MoveMemory(IntPtr destination, IntPtr source, IntPtr length);
        
        // dwSize - basically size will be int (for 32-bit) and long (for 64-bit)
        [DllImport("kernel32.dll", EntryPoint = "RtlFillMemory", SetLastError = false)]
        public static extern void FillMemory(IntPtr lpAddress, IntPtr dwSize, byte fill);

        // dwSize - basically size will be int (for 32-bit) and long (for 64-bit)
        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
        public static extern void ZeroMemory(IntPtr lpAddress, IntPtr dwSize);
    }

    #endregion

    
    #region ArrayNoLOH

    //////////////////////////////////////////////////////////////////////////////////////////////
    // This class is intended to be used in place of the built-in .NET Framework value-type
    // arrays for large arrays that would normally go on the Large Object Heap (85,000+ bytes).
    // It's meant to replace VALUE-TYPE arrays ONLY (i.e. byte[], int[], long[], double[], etc.)
    // Its purpose is to give large array functionality without using the Large Object Heap,
    // thereby eliminating Large Object Heap fragmentation. This class is backed by unmanaged
    // memory (VirtualAlloc), instead of managed memory (Large Object Heap). It has an indexer
    // and is enumerable implementing IList, so it can pretty much be used kind of like a regular
    // .NET array. It does not use a heap, instead it goes directly to VirtualAlloc to get
    // unmanaged memory to back this array class and releases that unmanaged memory in the
    // Dispose method when this array class is disposed. Not using a heap (managed Large Object
    // Heap, or unmanaged OS heap), allows this array class to avoid heap fragmentation for
    // large arrays (managed Large Object Heap fragmentation, or unmanaged OS heap fragmentation).
    // It's NOT intended to be used for small arrays, only large arrays that would go on the
    // Large Object Heap. Although it will still work for small arrays, it's far to inefficient
    // to be used for small arrays. VirtualAlloc allocates memory in pages that are rather large
    // (4KB/64KB), so using this class for a small 4 byte byte array for example would be
    // extremely wasteful. You need to still use a heap (Small Object Heap) that can sub-allocate
    // only the 4 bytes needed without wasting the rest of the page.
    // 
    // This class is backed by unmanaged memory which is not from a heap, goes directly to
    // VirtualAlloc (so no heap fragmentation). It's allocated in the constructor, and released
    // in the Dispose(). This class always goes on the Small Object Heap and disposes when the
    // Small Object Heap is collected by the GC. The unmanaged memory is released when the
    // small object is collected (so no memory leak). The GC thinks it's a small object (because
    // it doesn't know about the unmanaged memory). It essentially allows you to have a very large
    // array like object that does not go on the Large Object Heap and does not leak memory
    // like pointers in the old days if you forget to free them. You use this class in place of
    // very large arrays, so it doesn't go on the Large Object Heap. For example instead of this:
    // byte[] b = new byte[1000000], do this: ArrayNoLOH<byte> b = new ArrayNoLOH<byte>(1000000).
    // 
    // !! Only use this class for arrays that are greater than 85,000 TOTAL bytes !!
    //         (For example: byte[100000], int[25000], long[12500], etc.)
    //            (!! Don't use for small arrays like byte[4], etc. !!)
    //
    // !! Only supports value-types right now, NOT reference-types. To add additional support
    //    for more value-types, see instructions below. !!
    //
    // Instructions:
    //
    //   For each new value-type you want to support, simply add a new IIndexer<value-type>
    //   to class declaration for ArrayNoLOH<T>, and add the new IIndexer<value-type>
    //   implementation for that value-type down in this class. OPTIONALLY, add new pair of
    //   explicit casting operators if want to support casting to and from .NET array type
    //   (i.e. ArrayNoLOH<byte> to byte[], and byte[] to ArrayNoLOH<byte>, etc...) Probably
    //   not highly recommended because makes easy to accidentally get stuff on Large
    //   Object Heap without even realizing it. That's one of reasons made explicit operator,
    //   instead of implicit operator, so user would at least have to make a conscious decision
    //   to explicitly cast.
    //
    // How to use:
    //
    //    using (ArrayNoLOH<byte> bytes = new ArrayNoLOH<byte>(100000))
    //    {
    //        for (int i = 0 ; i < bytes.Count ; i++)
    //        {
    //            bytes[i] = 0x1;
    //        }
    //        for (int i = 0 ; i < bytes.Count ; i++)
    //        {
    //            Debug.WriteLine(bytes[i].ToString("x02"));
    //        }
    //        foreach (byte b in bytes)
    //        {
    //            Debug.WriteLine(b.ToString("x02"));
    //        }
    //    }
    //
    //    Instead of this:
    //
    //        byte[] bytes = new byte[100000];
    //        for (int i = 0 ; i < bytes.GetLength(0) ; i++)
    //        {
    //            bytes[i] = 0x1;
    //        }
    //        for (int i = 0 ; i < bytes.GetLength(0) ; i++)
    //        {
    //            Debug.WriteLine(bytes[i].ToString("x02"));
    //        }
    //        foreach (byte b in bytes)
    //        {
    //            Debug.WriteLine(b.ToString("x02"));
    //        }
    //        bytes = null;
    //
    // Important: Make sure to compile this class in Release mode for accurate
    // comparisons when benchmarking. Debug compiled versions of this class are
    // much slower than Release compiled versions.
    //////////////////////////////////////////////////////////////////////////////////////////////

    public class ArrayNoLOH<T> : IDisposable, IDisposableList<T>,
        IIndexer<byte>, IIndexer<int>, IIndexer<long>, IIndexer<double>
        where T : struct //this constrains T to only allow user to use value-type (i.e. byte, int, long, etc...)
    {
        private IntPtr _array = IntPtr.Zero;
        private int _length = 0;
        private int _size = 0;
        private EnumerationBehavior _enumerationBehavior = EnumerationBehavior.DisposeAfterEnumerated;

        /// <summary>
        /// Change to true if want to add pressure to GC also...this doesn't cause array to 
        /// use managed memory from GC to back the array, but does add the count of bytes of
        /// unmanaged memory used in array to the GC's calculations used to determine when to
        /// do a GC collection. It just gives the GC a hint (byte count) to use in its
        /// collection calculations, it doesn't cause array to use any GC memory to back
        /// the array.
        /// </summary>
        private bool _gcPressure = false;

        
        public ArrayNoLOH(int length)
            : this(length, EnumerationBehavior.DisposeAfterEnumerated)
        {
        }
        public ArrayNoLOH(int length, EnumerationBehavior enumerationBehavior)
        {
            _size = Marshal.SizeOf(typeof(T));
            _length = length;
            _enumerationBehavior = enumerationBehavior;

            _array = Win32.VirtualAlloc(IntPtr.Zero, (IntPtr)(_length * _size), Win32.MEM_RESERVE | Win32.MEM_COMMIT, Win32.PAGE_READWRITE);
            if (_array == IntPtr.Zero)
                throw new Exception("Allocation request failed.", new Win32Exception(Marshal.GetLastWin32Error()));

            if (_gcPressure) GC.AddMemoryPressure(_length * _size);
        }

        /// <summary>
        /// Releases all unmanaged memory backing this array.
        /// </summary>
        private void Free()
        {
            if (!Win32.VirtualFree(_array, IntPtr.Zero, Win32.MEM_RELEASE))
                if (!Environment.HasShutdownStarted)
                    throw new Exception("Free allocation failed.", new Win32Exception(Marshal.GetLastWin32Error()));

            if (_gcPressure) GC.RemoveMemoryPressure(_length * _size);

            _length = 0;
        }

        /// <summary>
        /// Gets pointer for unmanaged memory backing this array.
        /// </summary>
        public IntPtr AddressOf { get { return _array; } }

        #region IList

        /////////////////////////
        // IIndexer<T> is basically to get around the problem of not being able
        // to cast to type T. For each new value-type you want to support, you
        // simply add another IIndexer<type> interface to this ArrayNoLOH<T> class
        // declaration, and specify how that specific type is going to be parsed
        // in the actual implemention of that IIndexer<type> interface.
        // We lose some overall throughput speed and efficiency going through these
        // extra interfaces. So, if speed is the highest concern and much more
        // important than style of the code, you could re-write ArrayNoLOH<T> to be
        // a separate class for each value-type, putting the type parsing logic
        // directly in the main indexer. (e.g. re-write ArrayNoLOH<T> to ByteArrayNoLOH,
        // IntArrayNoLOH, LongArrayNoLOH, etc.) When you benchmark to compare, make
        // sure you compile in Release mode for an accurate comparison.
        /////////////////////////
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= this.Count)
                    throw new IndexOutOfRangeException();

                return ((IIndexer<T>)this)[index];
            }
            set
            {
                if (index < 0 || index >= this.Count)
                    throw new IndexOutOfRangeException();

                ((IIndexer<T>)this)[index] = value;
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            try
            {
                for (int i = 0; i < _length; i++)
                {
                    yield return this[i];
                }
            }
            finally
            {
                if (_enumerationBehavior == EnumerationBehavior.DisposeAfterEnumerated) Dispose();
            }
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Count of items in the array.
        /// </summary>
        public int Count
        {
            get { return _length; }
        }

        #region ICollection

        /// <summary>
        /// Inserts item into array at the specified index.
        /// </summary>
        public void Insert(int index, T item)
        {
            this[index] = item;
        }
        /// <summary>
        /// Sets all bytes of unmanaged memory backing the array to zero's.
        /// Doesn't release the memory, simply fills all memory with zero's.
        /// Is very efficient using FillMemory in Win32 API.
        /// </summary>
        public void Clear()
        {
            Win32.FillMemory(_array, (IntPtr)(_length * _size), 0x0);
        }
        /// <summary>
        /// Copies this source ArrayNoLOH(T) to a destination ArrayNoLOH(T) without using
        /// Large Object Heap.
        /// 
        /// Caution: This can be dangerous. Be extremely careful not to copy outside the bounds
        /// of the destination array.
        /// </summary>
        /// <param name="array">Destination ArrayNoLOH(T).</param>
        /// <param name="arrayIndex">Starting index inside destination ArrayNoLOH(T) to begin copying this entire source ArrayNoLOH(T).</param>
        public void CopyTo(ArrayNoLOH<T> array, int arrayIndex)
        {
            if (this.Count <= 0)
                throw new Exception("Nothing in source array to copy to destination array.");
            if (array.Count <= 0)
                throw new Exception("Destination array empty. Destination array must be large enough to hold source array.");
            if (arrayIndex < 0 || arrayIndex >= array.Count - 1)
                throw new IndexOutOfRangeException();
            if ((arrayIndex + this.Count) > array.Count)
                throw new ArgumentException("Destination array being copied to is not large enough to hold source array, when starting copy at specified arrayIndex.", "array");

            Win32.MoveMemory(array.AddressOf + arrayIndex, _array, (IntPtr)(_length * _size));
        }
        public bool IsReadOnly
        {
            get { return false; }
        }

        #region NotImplemented

        /// <summary>
        /// CopyTo(T[] array, int arrayIndex) method not supported.
        /// Must use CopyTo(ArrayNoLOH(T) array, int arrayIndex) method to stay off Large Object Heap.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void CopyTo(T[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Add(T item) method not supported.
        /// Must use array indexer (example: array[0]=0x1) to add item at the specified index.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void Add(T item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// RemoveAt(int index) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void RemoveAt(int index)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Remove(T item) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool Remove(T item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Contains(T item) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool Contains(T item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// IndexOf(T item) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public int IndexOf(T item)
        {
            throw new NotImplementedException();
        }

        #endregion
        #endregion
        #endregion

        #region IIndexers

        //////////////////////////
        // Instructions:
        //
        //   For each new IIndexer<type> you add to ArrayNoLOH<T> class declaration
        //   on top of class, add a new IIndexer<type> implementation here...
        //
        //////////////////////////
        ///////////////////////////////////////////////////////////////////////
        // IIndexer<T> is basically to get around the problem of not being able
        // to cast to a type T. For each new value-type you want to support, you
        // simply add another IIndexer<type> interface to the ArrayNoLOH<T> class
        // declaration, and specify how that specific type is going to be parsed
        // right here in the actual implemention of that IIndexer<type> interface.
        // We lose some overall throughput speed and efficiency going through these
        // extra interfaces. So, if speed is the highest concern and much more
        // important than style of the code, you could re-write ArrayNoLOH<T> to be
        // a separate class for each value-type, putting the type parsing logic
        // directly in the main indexer. (e.g. re-write ArrayNoLOH<T> to ByteArrayNoLOH,
        // IntArrayNoLOH, LongArrayNoLOH, etc.) When you benchmark to compare, make
        // sure you compile in Release mode for an accurate comparison.        
        ///////////////////////////////////////////////////////////////////////

        unsafe byte IIndexer<byte>.this[int index]
        {
            get { return *((byte*)(_array + (int)(index * _size))); }
            set { *((byte*)(_array + (int)(index * _size))) = value; }
        }

        unsafe int IIndexer<int>.this[int index]
        {
            get { return *((int*)(_array + (int)(index * _size))); }
            set { *((int*)(_array + (int)(index * _size))) = value; }
        }

        unsafe long IIndexer<long>.this[int index]
        {
            get { return *((long*)(_array + (int)(index * _size))); }
            set { *((long*)(_array + (int)(index * _size))) = value; }
        }

        unsafe double IIndexer<double>.this[int index]
        {
            get { return *((double*)(_array + (int)(index * _size))); }
            set { *((double*)(_array + (int)(index * _size))) = value; }
        }

        #endregion

        #region Casting Operators

        //////////////////////////
        // Instructions:
        //
        //   For each new IIndexer<type> you add to ArrayNoLOH<T> class declaration on top,
        //   add the new pair of explicit casting operators here if you want to support
        //   casting to and from the .NET array type (i.e. ArrayNoLOH<byte> to byte[],
        //   and byte[] to ArrayNoLOH<byte>, etc...)
        //
        //////////////////////////
        ///////////////////////////////////////////////////////////////////////
        // Important: These are here purely for convience. They are simply to
        // make it easy to cast back and forth between this ArrayNoLOH(T)
        // class and the built-in .NET Framework array types (i.e. byte[],
        // int[], long[], etc.) for integrating with existing methods with
        // method parameters that only accept built-in .NET Framework array
        // types. These casting operators are not good. They basically defeat
        // the entire purpose of this class, because they must either create
        // a new .NET Framework array type to return for the cast (which goes
        // on Large Object Heap for large arrays), or make a duplicate second
        // copy in unmanaged memory for casting the other way.
        // 
        // Note: Need to add a new pair of these casting operators when you add
        // a new IIndexer(T) that supports a new value-type if you want to have
        // the ability to cast to and from that .NET Framework value-type array.
        // 
        // Caution: Only use these casting operators if there's no other way
        // to do what you're trying accomplish. Using these is either very
        // inefficient with memory, or can cause stuff to go on the Large
        // Object Heap. May NOT want to support these casting operation at all
        // because of thier impact on the Large Object Heap. It may actually
        // make it to easy for an end-user to simply cast this array to a .NET
        // array which will go on the Large Object Heap, without even realizing it.
        //
        // For a fun kind of out there future enhancement that just might be
        // possible but would require some work would be to possibly modify the
        // coreclr and corefx open-source code so there's a bit that can be set
        // in the object header for the built-in .NET Framework value-type array
        // object, which tells the GC to ignore this object when it does a GC
        // collection. That would allow use to do what we call 'Type-Facing'.
        // 'Type-Facing' is essentially creating a null .Net value-type array
        // (i.e. byte[] b = null), adding the object header, method table
        // and length section of bytes from the .Net value-type byte[] to the
        // beginning of the unmanaged memory backing our ArrayNoLOH(T) array,
        // and changing the pointer of that .Net value-type byte[] to the address
        // of our unmanaged memory backing our ArrayNoLOH(T) array. This worked
        // in our tests, our ArrayNoLOH(T) array now looked and functioned like
        // the .Net value-type byte[] (e.g. our ArrayNoLOH(T) array now
        // essentially had a 'Face' of a .NET byte[]), however the GC would still
        // collect the .NET byte[] when the GC decided to do a GC collection at
        // various times because the .NET byte[] was pointing at unmanaged memory
        // whose memory address was outside the begin and ending address range of
        // the managed memory the GC manages and the GC would think it's ok to
        // clean up and destroy the object. At that point, even though our
        // unmanaged memory was still in place, the .NET byte[] object was now
        // unusuable.
        ///////////////////////////////////////////////////////////////////////
        public static explicit operator byte[](ArrayNoLOH<T> array)
        {
            byte[] a = new byte[array.Count];
            Marshal.Copy(array.AddressOf, a, 0, array.Count);
            return a;
        }
        public static explicit operator ArrayNoLOH<T>(byte[] array)
        {
            ArrayNoLOH<T> a = new ArrayNoLOH<T>(array.GetLength(0));
            Marshal.Copy(array, 0, a.AddressOf, array.GetLength(0));
            return a;
        }

        public static explicit operator int[](ArrayNoLOH<T> array)
        {
            int[] a = new int[array.Count];
            Marshal.Copy(array.AddressOf, a, 0, array.Count);
            return a;
        }
        public static explicit operator ArrayNoLOH<T>(int[] array)
        {
            ArrayNoLOH<T> a = new ArrayNoLOH<T>(array.GetLength(0));
            Marshal.Copy(array, 0, a.AddressOf, array.GetLength(0));
            return a;
        }

        public static explicit operator long[](ArrayNoLOH<T> array)
        {
            long[] a = new long[array.Count];
            Marshal.Copy(array.AddressOf, a, 0, array.Count);
            return a;
        }
        public static explicit operator ArrayNoLOH<T>(long[] array)
        {
            ArrayNoLOH<T> a = new ArrayNoLOH<T>(array.GetLength(0));
            Marshal.Copy(array, 0, a.AddressOf, array.GetLength(0));
            return a;
        }

        public static explicit operator double[](ArrayNoLOH<T> array)
        {
            double[] a = new double[array.Count];
            Marshal.Copy(array.AddressOf, a, 0, array.Count);
            return a;
        }
        public static explicit operator ArrayNoLOH<T>(double[] array)
        {
            ArrayNoLOH<T> a = new ArrayNoLOH<T>(array.GetLength(0));
            Marshal.Copy(array, 0, a.AddressOf, array.GetLength(0));
            return a;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ArrayNoLOH()
        {
            Dispose(false);
        }

        private bool _disposed = false;

        private void Dispose(bool disposing)
        {
            if (!this._disposed)
            {
                if (disposing)
                {
                    //managed here...
                }

                //unmanaged here...

                Free();
            }
            this._disposed = true;
        }

        #endregion
    }

    #endregion

    
    #region MemoryStreamNoLOH

    //////////////////////////////////////////////////////////////////////////////////////////////
    // This class is intended to be used in place of the built-in MemoryStream .NET Framework
    // class, for anything requiring a MemoryStream, like binary serialization of a collection,
    // etc. Its purpose is to keep these things off the Large Object Heap. It's intended to be
    // used for large things that would otherwise normally go on the Large Object Heap. For binary
    // serialization of a large collection for example, the regular MemoryStream .NET Framework
    // class uses an internal byte array buffer that will go on the Large Object Heap if it's
    // over 85,000 bytes. This class is a memory stream that's backed by unmanaged memory (VirtualAlloc)
    // and is expandable in segment size blocks. The default segment size is 1MB, however
    // the user can supply their own custom smaller or larger segment size. Being backed by
    // unmanaged memory keeps it off the Large Object Heap. The unmanaged memory is released
    // (VirtualFree) when the stream is Disposed. The use of Using blocks is highly recommended
    // when using this class so the unmanaged memory is released as soon as possible.
    // 
    // The regular MemoryStream .NET Framework class has two modes: a fixed buffer mode, and an
    // expandable buffer mode. If the user supplies an external buffer, it's set to fixed buffer
    // mode and it uses that external buffer. If no buffer is supplied, it's set to expandable
    // buffer mode and creates its own internal byte array buffer that's expanded when necessary.
    // This MemoryStreamNoLOH class tries to mimic the regular MemoryStream .NET Framework class
    // as close as possible. This way the user can pretty much use it the same way and in the
    // same places in code as the regular MemoryStream class as seamlessly as possible with
    // minimal changes required to their existing code. This MemoryStreamNoLOH class has only
    // one mode, expandable mode. It generates its own internal list of unmanaged memory segment
    // size buffers that functions as an adjustable size buffer that will keep expanding as
    // necessary. The built-in UnmanagedMemoryStream .NET Framework class is used for what 
    // would essentially be the fixed mode portion. Originally the fixed mode portion was also
    // implemented in the MemoryStreamNoLOH class, but thought it better to not
    // 'recreate-the-wheel' whenever possible and removed it. Instead, use the
    // UnmanagedMemoryStream .NET Framework class (if you don't mind marking your code with
    // 'unsafe'), or the UnmanagedMemoryStreamEx wrapper (no 'unsafe' required) for the
    // UnmanagedMemoryStream .NET Framework class. The UnmanagedMemoryStream .NET Framework class
    // does NOT make a copy of the memory and store it in the stream, it simply allows you to
    // do stream operations on the existing memory.
    // 
    // Caution: Must be extremely careful whenever using pointers to unmanaged memory inside
    // managed code. Small Object Heap can move its managed memory around at any time to
    // compact heap.
    // 
    // !! Only use this class for streams that are greater than 85,000 TOTAL bytes !!
    //      (For example: 100000 bytes after serialization, 1000000 bytes,  etc.)
    //          (!! Don't use for streams with small number of bytes after
    //              serialization like 1000 bytes, 2000 bytes, etc. !!)
    //
    // How to use:
    //
    //    private ArrayNoLOH<byte> SerializeNoLOH(object item)
    //    {
    //        using (MemoryStreamNoLOH ms = new MemoryStreamNoLOH())
    //        {
    //            BinaryFormatter binary = new BinaryFormatter();
    //            binary.Serialize(ms, item);
    //            ArrayNoLOH<byte> bytes = ms.ToArray();
    //            return bytes;
    //        }
    //    }
    //    private object DeserializeNoLOH(ArrayNoLOH<byte> bytes)
    //    {
    //        using (UnmanagedMemoryStreamEx ms = new UnmanagedMemoryStreamEx(bytes))
    //        {
    //            BinaryFormatter binary = new BinaryFormatter();
    //            return binary.Deserialize(ms);
    //        }
    //    }
    //
    //    or this:
    //
    //    private MemoryStreamNoLOH SerializeNoLOHStream(object item)
    //    {
    //        MemoryStreamNoLOH ms = new MemoryStreamNoLOH();
    //        BinaryFormatter binary = new BinaryFormatter();
    //        binary.Serialize(ms, item);
    //        return ms;
    //    }
    //    private object DeserializeNoLOHStream(MemoryStreamNoLOH stream)
    //    {
    //        using (stream) //must Dispose() stream to release unmanaged memory
    //        {
    //            stream.Position = 0;
    //            BinaryFormatter binary = new BinaryFormatter();
    //            return binary.Deserialize(stream);
    //        }
    //    }
    //
    //    Instead of this:
    //
    //       private byte[] Serialize(object item)
    //       {
    //           using (MemoryStream ms = new MemoryStream()) //MemoryStream uses Large Object Heap when serialization's over 85,000 bytes
    //           {
    //               BinaryFormatter binary = new BinaryFormatter();
    //               binary.Serialize(ms, item);
    //               byte[] bytes = ms.ToArray(); //byte[] uses Large Object Heap when serialization's over 85,000 bytes
    //               return bytes;
    //           }
    //       }
    //       private object Deserialize(byte[] bytes)
    //       {
    //           using (MemoryStream ms = new MemoryStream(bytes))
    //           {
    //               BinaryFormatter binary = new BinaryFormatter();
    //               return binary.Deserialize(ms);
    //           }
    //       }
    //
    // Important: Make sure to compile this class in Release mode for accurate
    // comparisons when benchmarking. Debug compiled versions of this class are
    // much slower than Release compiled versions.
    //////////////////////////////////////////////////////////////////////////////////////////////

    public class MemoryStreamNoLOH : Stream
    {
        private List<IntPtr> _segments = new List<IntPtr>();
        private long _segmentSize = 0;
        private long _capacity = 0;
        private long _length = 0;
        private long _position = 0;
        private bool _open = false;

        //buffer to re-use for ReadByte and WriteByte
        private byte[] _readByte = new byte[1];
        private byte[] _writeByte = new byte[1];

        //Change to true if want to add pressure to GC also...this doesn't cause stream to 
        //use managed memory from GC to back the stream, but does add the count of bytes of
        //unmanaged memory used in stream to the GC's calculations used to determine when to
        //do a GC collection. It just gives the GC a hint (byte count) to use in its
        //collection calculations, it doesn't cause stream to use any GC memory to back
        //the stream.
        private bool _gcPressure = false;

        /// <summary>
        /// Initializes a stream that does not use Large Object Heap in GC.
        /// Stream is initialized as expandable, with a default segment size of 1MB.
        /// 
        /// Note: Only use this stream for operations requiring buffers larger than 85,000 bytes
        /// (i.e. buffers that would normally go on the Large Object Heap).
        /// </summary>
        public MemoryStreamNoLOH()
        {
            //default segment size
            //_segmentSize = 1024 * 64; //64KB
            _segmentSize = 1048576 * 1; //1MB

            IntPtr segment = Win32.VirtualAlloc(IntPtr.Zero, (IntPtr)_segmentSize, Win32.MEM_RESERVE | Win32.MEM_COMMIT, Win32.PAGE_READWRITE);
            if (segment == IntPtr.Zero)
                throw new Exception("Allocation request failed.", new Win32Exception(Marshal.GetLastWin32Error()));

            _segments.Add(segment);
            _capacity = _segmentSize;

            _open = true;

            if (_gcPressure) GC.AddMemoryPressure(_capacity);
        }
        /// <summary>
        /// Initializes a stream that does not use Large Object Heap in GC.
        /// Stream is initialized as expandable, with a user specified segment size.
        /// 
        /// Stream automatically expands in segment size blocks as needed as
        /// data is written to the stream. Larger segment sizes like 1MB or 10MB
        /// are usually much more efficient and generally preferable.
        /// 
        /// Note: Only use this stream for operations requiring buffers larger than 85,000 bytes
        /// (i.e. buffers that would normally go on the Large Object Heap).
        /// 
        /// Note: Memory is 4KB page, virtual addresses are 64KB minimum granularity
        /// (be careful because lengths below these can really waste memory and virtual addresses).
        /// Large Object Heap is 85,000+ bytes.
        /// </summary>
        /// <param name="segmentSize">
        /// Size of individual segment blocks of unmanaged memory stored in list backing stream.
        /// Must be between 65536 (64KB) and int.MaxValue (32-bit) or long.MaxValue (64-bit).
        /// </param>
        public MemoryStreamNoLOH(long segmentSize)
        {
            if (segmentSize < 65536 || segmentSize > (IntPtr.Size == 4 ? int.MaxValue : long.MaxValue))
                throw new ArgumentOutOfRangeException("segmentSize", "Segment size must be between 65536 (64KB) and int.MaxValue (32-bit) or long.MaxValue (64-bit).");

            _segmentSize = segmentSize;

            IntPtr segment = Win32.VirtualAlloc(IntPtr.Zero, (IntPtr)_segmentSize, Win32.MEM_RESERVE | Win32.MEM_COMMIT, Win32.PAGE_READWRITE);
            if (segment == IntPtr.Zero)
                throw new Exception("Allocation request failed.", new Win32Exception(Marshal.GetLastWin32Error()));

            _segments.Add(segment);
            _capacity = _segmentSize;

            _open = true;

            if (_gcPressure) GC.AddMemoryPressure(_capacity);
        }
        /// <summary>
        /// Releases all memory backing this stream.
        /// </summary>
        private void Free()
        {
            _open = false;

            for (int i = 0; i < _segments.Count; i++)
            {
                if (!Win32.VirtualFree(_segments[i], IntPtr.Zero, Win32.MEM_RELEASE))
                    if (!Environment.HasShutdownStarted)
                        throw new Exception("Free allocation failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (_gcPressure) GC.RemoveMemoryPressure(_capacity);

            _position = 0;
            _length = 0;
            _capacity = 0;
        }

        /// <summary>
        /// Reads a block of bytes from the stream, using byte array as destination buffer.
        /// Returns number of bytes read from the stream into the byte array buffer.
        /// </summary>
        /// <param name="buffer">Destination buffer used to read from stream.</param>
        /// <param name="offset">Starting position of block of bytes inside destination buffer that will be used to read from stream.</param>
        /// <param name="count">Size of block of bytes inside destination buffer used to read from stream.</param>
        unsafe public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null || buffer.Length == 0)
                throw new ArgumentException("Valid buffer required.", "buffer");
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "Negative offset is not allowed.");
            if (count <= 0)
                throw new ArgumentOutOfRangeException("count", "Count must be greater than 0.");


            //can't read past end of the total of all segments (end of stream)
            long total = _segmentSize * (long)_segments.Count;
            if ((_position + (long)count) > total)
                throw new EndOfStreamException("Attempted to read past end of internal buffer (end of stream).");

            //copied data
            long copied = 0;
            while (copied < (long)count)
            {
                //calc position
                int segmentIndex = (int)((double)_position / (double)_segmentSize);
                long positionInSegment = _position % _segmentSize;
                long remainderInSegment = _segmentSize - positionInSegment;

                //if remaining data to copy is less than the remainder in segment
                //truncate remainder in segment to end of data
                if (((long)count - copied) < remainderInSegment)
                    remainderInSegment = ((long)count - copied);

                //copy data
                //Caution: Must be very careful whenever using pointers in managed code.
                //Small Object Heap can move its managed memory around at any time to compact heap.
                fixed (byte* b = &buffer[offset + (int)copied])
                {
                    Win32.MoveMemory((IntPtr)b, _segments[segmentIndex] + (int)positionInSegment, (IntPtr)remainderInSegment);
                }
                //safe code version
                //Marshal.Copy(_segments[segmentIndex] + (int)positionInSegment, buffer, offset + (int)copied, (int)remainderInSegment);

                //adjust position
                _position += remainderInSegment;

                //adjust amount copied
                copied += remainderInSegment;
            }
            return count;
        }
        /// <summary>
        /// Returns the byte that was read, or -1 if no more bytes.
        /// </summary>
        public override int ReadByte()
        {
            _readByte[0] = 0x0;
            if (Read(_readByte, 0, 1) < 1)
                return -1;
            return (int)_readByte[0];
        }
        /// <summary>
        /// Reads a block of bytes from the stream, using byte array pointer as destination buffer.
        /// Returns number of bytes read from the stream to the byte array pointer buffer.
        /// 
        /// Caution: Must be extremely careful whenever using pointers to unmanaged memory
        /// inside managed code. Watch out for extremely dangerous things like offset+count
        /// exceeding the memory allocated for the buffer (thereby causing bytes from the 
        /// stream to be read into unknown memory past the end of the buffer), and offset
        /// being negative (thereby causing bytes from the stream to be read into unknown
        /// memory before the beginning of the buffer), etc.
        /// </summary>
        /// <param name="buffer">Destination buffer used to read from stream.</param>
        /// <param name="offset">Starting position of block of bytes inside destination buffer that will be used to read from stream.</param>
        /// <param name="count">Size of block of bytes inside destination buffer used to read from stream.</param>
        unsafe public int Read(byte* buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "Negative offset is not allowed.");
            if (count <= 0)
                throw new ArgumentOutOfRangeException("count", "Count must be greater than 0.");


            //can't read past end of the total of all segments (end of stream)
            long total = _segmentSize * (long)_segments.Count;
            if ((_position + (long)count) > total)
                throw new EndOfStreamException("Attempted to read past end of internal buffer (end of stream).");

            //copied data
            long copied = 0;
            while (copied < (long)count)
            {
                //calc position
                int segmentIndex = (int)((double)_position / (double)_segmentSize);
                long positionInSegment = _position % _segmentSize;
                long remainderInSegment = _segmentSize - positionInSegment;

                //if remaining data to copy is less than the remainder in segment
                //truncate remainder in segment to end of data
                if (((long)count - copied) < remainderInSegment)
                    remainderInSegment = ((long)count - copied);

                //copy data
                Win32.MoveMemory((IntPtr)buffer + (int)(offset + (int)copied), _segments[segmentIndex] + (int)positionInSegment, (IntPtr)remainderInSegment);

                //adjust position
                _position += remainderInSegment;

                //adjust amount copied
                copied += remainderInSegment;
            }
            return count;
        }

        /// <summary>
        /// Writes a block of bytes to the stream, using the byte array as source buffer.
        /// </summary>
        /// <param name="buffer">Source buffer to write to stream.</param>
        /// <param name="offset">Starting position of block of bytes inside source buffer that will be written to stream.</param>
        /// <param name="count">Size of block of bytes inside source buffer to write to stream.</param>
        unsafe public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null || buffer.Length == 0)
                throw new ArgumentException("Valid buffer required.", "buffer");
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "Negative offset is not allowed.");
            if (count <= 0)
                throw new ArgumentOutOfRangeException("count", "Count must be greater than 0.");

            
            //add segments if required
            long total = _segmentSize * (long)_segments.Count;
            while ((_position + (long)count) > total)
            {
                IntPtr segment = Win32.VirtualAlloc(IntPtr.Zero, (IntPtr)_segmentSize, Win32.MEM_RESERVE | Win32.MEM_COMMIT, Win32.PAGE_READWRITE);
                if (segment == IntPtr.Zero)
                    throw new Exception("Allocation request failed.", new Win32Exception(Marshal.GetLastWin32Error()));

                _segments.Add(segment);
                _capacity += _segmentSize;

                total += _segmentSize;

                if (_gcPressure) GC.AddMemoryPressure(_segmentSize);
            }

            //copy data
            long copied = 0;
            while (copied < (long)count)
            {
                //calc position
                int segmentIndex = (int)((double)_position / (double)_segmentSize);
                long positionInSegment = _position % _segmentSize;
                long remainderInSegment = _segmentSize - positionInSegment;

                //if remaining data to copy is less than the remainder in segment
                //truncate remainder in segment to end of data
                if (((long)count - copied) < remainderInSegment)
                    remainderInSegment = ((long)count - copied);

                //copy data
                //Caution: Must be very careful whenever using pointers in managed code.
                //Small Object Heap can move its managed memory around at any time to compact heap.
                fixed (byte* b = &buffer[offset + (int)copied])
                {
                    Win32.MoveMemory(_segments[segmentIndex] + (int)positionInSegment, (IntPtr)b, (IntPtr)remainderInSegment);
                }
                //safe code version
                //Marshal.Copy(buffer, offset + (int)copied, _segments[segmentIndex] + (int)positionInSegment, (int)remainderInSegment);

                //adjust position
                _position += remainderInSegment;

                //always keep length at the highest ever write position
                if (_position > _length)
                    _length = _position;

                //adjust amount copied
                copied += remainderInSegment;
            }
        }
        public override void WriteByte(byte value)
        {
            _writeByte[0] = value;
            Write(_writeByte, 0, 1);
        }
        /// <summary>
        /// Writes a block of bytes to the stream, using the byte array pointer as source buffer.
        /// 
        /// Caution: Must be extremely careful whenever using pointers to unmanaged memory
        /// inside managed code. Watch out for extremely dangerous things like offset+count
        /// exceeding the memory allocated for the buffer (thereby causing unknown memory
        /// past the end of the buffer to be written to the stream), and offset being
        /// negative (thereby causing unknown memory before the beginning of the buffer to be
        /// written to the stream), etc.
        /// </summary>
        /// <param name="buffer">Source buffer to write to stream.</param>
        /// <param name="offset">Starting position of block of bytes inside source buffer that will be written to stream.</param>
        /// <param name="count">Size of block of bytes inside source buffer to write to stream.</param>
        unsafe public void Write(byte* buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (offset < 0)
                throw new ArgumentOutOfRangeException("offset", "Negative offset is not allowed.");
            if (count <= 0)
                throw new ArgumentOutOfRangeException("count", "Count must be greater than 0.");


            //add segments if required
            long total = _segmentSize * (long)_segments.Count;
            while ((_position + (long)count) > total)
            {
                IntPtr segment = Win32.VirtualAlloc(IntPtr.Zero, (IntPtr)_segmentSize, Win32.MEM_RESERVE | Win32.MEM_COMMIT, Win32.PAGE_READWRITE);
                if (segment == IntPtr.Zero)
                    throw new Exception("Allocation request failed.", new Win32Exception(Marshal.GetLastWin32Error()));

                _segments.Add(segment);
                _capacity += _segmentSize;

                total += _segmentSize;

                if (_gcPressure) GC.AddMemoryPressure(_segmentSize);
            }

            //copy data
            long copied = 0;
            while (copied < (long)count)
            {
                //calc position
                int segmentIndex = (int)((double)_position / (double)_segmentSize);
                long positionInSegment = _position % _segmentSize;
                long remainderInSegment = _segmentSize - positionInSegment;

                //if remaining data to copy is less than the remainder in segment
                //truncate remainder in segment to end of data
                if (((long)count - copied) < remainderInSegment)
                    remainderInSegment = ((long)count - copied);

                //copy data
                Win32.MoveMemory(_segments[segmentIndex] + (int)positionInSegment, (IntPtr)buffer + (int)(offset + (int)copied), (IntPtr)remainderInSegment);

                //adjust position
                _position += remainderInSegment;

                //always keep length at the highest ever write position
                if (_position > _length)
                    _length = _position;

                //adjust amount copied
                copied += remainderInSegment;
            }
        }

        public override long Position
        {
            get { return _position; }
            set
            {
                if (value < 0 || value > _capacity)
                    throw new IndexOutOfRangeException();
                _position = value;
            }
        }
        /// <summary>
        /// Total length of the allocated memory backing this stream,
        /// NOT the count of bytes actually written to the memory.
        /// </summary>
        public long Capacity
        {
            get { return _capacity;  /* ((long)_segments.Count * _segmentSize) */ }
        }
        /// <summary>
        /// Count of bytes actually written to this stream.
        /// </summary>
        public override long Length
        {
            get { return _length; }
        }
        public long SegmentSize
        {
            get { return _segmentSize; }
        }
        public int SegmentCount
        {
            get { return _segments.Count; }
        }
        public override void Flush()
        {
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = _position;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;
                case SeekOrigin.Current:
                    position = _position + offset;
                    break;
                case SeekOrigin.End:
                    position = _capacity + offset;
                    break;
                default:
                    throw new NotSupportedException();
            }

            if (position < 0 || position > _capacity)
                throw new ArgumentOutOfRangeException("offset");

            _position = position;

            return _position;
        }
        public override bool CanRead
        {
            get { return _open; }
        }
        public override bool CanSeek
        {
            get { return _open; }
        }
        public override bool CanWrite
        {
            get { return _open; }
        }
        /// <summary>
        /// SetLength(long value) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// When no buffer size is supplied, base stream defaults buffer to the largest buffer
        /// that's a multiple of 4096 that's still under the Large Object Heap minimum of
        /// (85k), so it will stay on Small Object Heap and hopefully efficiently clean up in
        /// gen0. Defaults to 81920 bytes.
        /// </summary>
        new public void CopyTo(Stream destination)
        {
            base.CopyTo(destination);
        }
        /// <summary>
        /// Don't allow to set buffer size to size that's larger than the largest buffer
        /// that's a multiple of 4096 that's still under the Large Object Heap minimum of
        /// (85k), so it will stay on Small Object Heap and hopefully efficiently clean up in
        /// gen0.
        /// </summary>
        new public void CopyTo(Stream destination, int bufferSize)
        {
            if (bufferSize > 81920)
                throw new ArgumentOutOfRangeException("bufferSize", "Buffer size must be less than or equal to 81920, which is the largest buffer size that's a multiple of 4096 that's still under the Large Object Heap minimum of (85k).");

            base.CopyTo(destination, bufferSize);
        }

        #region Helper Methods

        /// <summary>
        /// Copies contents of stream into byte array buffer
        /// that does NOT use Large Object Heap in GC.
        /// </summary>
        public ArrayNoLOH<byte> ToArray()
        {
            ArrayNoLOH<byte> array = new ArrayNoLOH<byte>((int)_length);
            for (int i = 0; i < _segments.Count; i++)
            {
                Win32.MoveMemory(array.AddressOf + (int)((long)i * _segmentSize), _segments[i], (((long)i * _segmentSize) + _segmentSize) > _length ? (IntPtr)(_length - ((long)i * _segmentSize)) : (IntPtr)_segmentSize);
            }
            return array;
        }

        #endregion

        #region IDisposable

        /////////////////////////////
        // Dispose pattern is handled a little different when in a derived class, with a base class
        // that already implements IDisposable. In this derived class, the base class (stream)
        // already implements IDisposable. So docs says:
        // 1) This derived class does not need to inherit from and implement IDisposable itself.
        // 2) This derived class is not supposed to override the Close(). The Dispose() in the 
        //    base class (stream) already calls Close().
        // 3) This derived class is supposed to override the Dispose(bool disposing) and put all
        //    managed and unmanaged cleanup in there.
        /////////////////////////////

        //base class already has Dispose() that just calls Close()
        //that has this exact same code already
        //public void Dispose()
        //{
        //    Dispose(true);
        //    GC.SuppressFinalize(this);
        //}

        ~MemoryStreamNoLOH()
        {
            Dispose(false);
        }

        private bool _disposed = false;

        protected override void Dispose(bool disposing)
        {
            if (!this._disposed)
            {
                if (disposing)
                {
                    //managed here...
                }

                //unmanaged here...

                Free();

                base.Dispose(disposing);  //base class implementation is empty, but still says to call
            }
            this._disposed = true;
        }

        #endregion
    }

    #endregion

    
    #region UnmanagedMemoryStreamEx

    /// <summary>
    /// This class is merely a wrapper around the UnmanagedMemoryStream .NET Framework class,
    /// simply to allow clients to use the UnmanagedMemoryStream with our ArrayNoLOH(byte) class
    /// without having to mark the calling code in the client as unsafe. For our purposes, this
    /// class essentially substitutes as the equivalent of the fixed buffer mode portion of the
    /// MemoryStream .NET Framework class. The UnmanagedMemoryStream .NET Framework class does
    /// NOT make a copy of the memory and store it in the stream, it simply allows you to do
    /// stream operations on the existing memory.
    /// </summary>
    public class UnmanagedMemoryStreamEx : UnmanagedMemoryStream
    {
        //Right now defaults to automatically dispose ArrayNoLOH<byte> array passed into
        //constructor when this class is disposed, to make sure don't leak memory if
        //forget to dispose array.
        private ArrayNoLOH<byte> _array = null;
        private bool _automaticallyDisposeArray = true;

        private UnmanagedMemoryStreamEx()
        {
        }
        unsafe public UnmanagedMemoryStreamEx(ArrayNoLOH<byte> array)
            : base((byte*)array.AddressOf, array.Count)
        {
            _array = array;
        }
        unsafe public UnmanagedMemoryStreamEx(ArrayNoLOH<byte> array, bool automaticallyDisposeArray)
            : base((byte*)array.AddressOf, array.Count)
        {
            _array = array;
            _automaticallyDisposeArray = automaticallyDisposeArray;
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            //automatically dispose array when this class is disposed
            if (_automaticallyDisposeArray && _array != null)
            {
                _array.Dispose();
                _array = null;
            }
        }
    }

    #endregion

    
    #region StringArrayNoLOH

    //////////////////////////////////////////////////////////////////////////////////////////////
    // This class is used internally by the SplitNoLOH extension method. It's NOT meant to be
    // instantiated as a stand-alone instance. It's merely used as the return type of SplitNoLOH
    // (i.e. StringArrayNoLOH array = string.SplitNoLOH('^')), in place of return type of String[].
    // The current built-in string.Split() implementation in .NET CLR internally uses an int[] that's
    // set to the size of the string in order to keep track of the split delimiter index locations
    // within the string. This int[] ends up going on the Large Object Heap when the string is roughly
    // 21,250 (since the .NET int is 4 bytes), and can sometimes cause serious LOH problems when
    // splitting very large strings. Moreover, the string itself goes on the LOH when its over
    // roughly 42,500 characters (since the .NET char is 2 bytes for unicode).
    //
    // This StringArrayNoLOH class uses unmanaged memory that's not from a heap (directly from OS with
    // VirtualAlloc) to store the index position and length for all of the delimiter location's in the
    // large string being split, in place of that int[], in order to keep off the GC Large Object Heap.
    // The StringArrayNoLOH class then releases the unmanaged memory in its Dispose() method.
    //
    // This class is intended to be used ONLY for splitting very large strings (over approx. 21,250
    // characters). The int[21250]+ array that the regular .NET Split method utilizes in its internal
    // implementation code to store the character offset locations of the split delimiters, ends up
    // going on the Large Object Heap (since .NET int is 4 bytes). This class is used to function in
    // place of that int[21250] array and avoid the Large Object Heap.
    //
    // !! Important: Must make sure to call Dispose() on the StringArrayNoLOH class,
    //               or it will leak unmanaged memory.  Remember, be extra careful to look out for
    //               the following:  foreach(string item in text.SplitNoLOH('^')). It can cause
    //               a memory leak without even realizing it, if you don't specify
    //               EnumerationBehavior.DisposeAfterEnumerated, because the invisible
    //               StringArrayNoLOH instance that's returned from SplitNoLOH will not have Dispose()
    //               called automatically after the foreach loop ends and will consequently leak
    //               unmanaged memory.
    //////////////////////////////////////////////////////////////////////////////////////////////

    public class StringArrayNoLOH : IDisposable, IDisposableList<string>
    {
        private IntPtr _offsets = IntPtr.Zero;
        private string _string = string.Empty;
        private int _length = 0;
        private int _size = 0;
        private int _count = 0;
        private EnumerationBehavior _enumerationBehavior = EnumerationBehavior.DisposeAfterEnumerated;

        private bool _gcPressure = false;  //change to true if want to add pressure to GC also

        
        //Only use this class as the return value of string.SplitNoLOH
        //(don't allow user to create new stand alone instances).
        internal StringArrayNoLOH()
        {
            _enumerationBehavior = EnumerationBehavior.DoNotDisposeAfterEnumerated;
        }
        internal StringArrayNoLOH(string fullString, EnumerationBehavior enumerationBehavior)
        {
            _string = fullString;
            _length = _string.Length;
            _size = sizeof(int) + sizeof(int);
            _enumerationBehavior = enumerationBehavior;

            _offsets = Win32.VirtualAlloc(IntPtr.Zero, (IntPtr)(_length * _size), Win32.MEM_RESERVE | Win32.MEM_COMMIT, Win32.PAGE_READWRITE);
            if (_offsets == IntPtr.Zero)
                throw new Exception("Allocation request failed.", new Win32Exception(Marshal.GetLastWin32Error()));

            if (_gcPressure) GC.AddMemoryPressure(_length * _size);
        }

        private void Free()
        {
            if (!Win32.VirtualFree(_offsets, IntPtr.Zero, Win32.MEM_RELEASE))
                if (!Environment.HasShutdownStarted)
                    throw new Exception("Free allocation failed.", new Win32Exception(Marshal.GetLastWin32Error()));

            _count = 0;

            if (_gcPressure) GC.RemoveMemoryPressure(_length * _size);
        }

        /// <summary>
        /// This is used internally by SplitNoLOH extension method in order to
        /// add a Tuple(int,int) with the first int specifying the position of
        /// the delimiter in the string, and the second int specifying the length
        /// of the substring from that delimiter.
        /// </summary>
        /// <param name="offset">Tuple(int,int) where first int represents position of delimiter in string, and second int represents length of substring from that delimiter.</param>
        internal unsafe void AddOffset(Tuple<int, int> offset)
        {
            int position = offset.Item1;
            int length = offset.Item2;

            *((int*)(_offsets + (int)(_count * _size))) = position;
            *((int*)(_offsets + (int)(_count * _size) + sizeof(int))) = length;

            _count++;
        }

        #region IList

        public unsafe string this[int index]
        {
            get
            {
                if (index >= _count)
                    throw new IndexOutOfRangeException();

                int* offset = (int*)(_offsets + (int)(index * _size));
                int position = *offset;
                int* offset2 = (int*)(_offsets + (int)(index * _size) + sizeof(int));
                int length = *offset2;
                return _string.Substring(position, length);
            }
            set
            {
                throw new Exception("Adding string through indexer not supported. Must use AddOffset() method.");
            }
        }

        public int Count
        {
            get { return _count; }
        }

        public IEnumerator<string> GetEnumerator()
        {
            try
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return this[i];
                }
            }
            finally
            {
                if (_enumerationBehavior == EnumerationBehavior.DisposeAfterEnumerated) Dispose();
            }
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        #region NotImplemented

        /// <summary>
        /// Add(string item) method not supported. Must use AddOffset() method.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void Add(string item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Insert(int index, string item) method not supported. Must use AddOffset() method.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void Insert(int index, string item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Remove(string item) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool Remove(string item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// RemoveAt(int index) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void RemoveAt(int index)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Clear() method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void Clear()
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Contains(string item) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool Contains(string item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// IndexOf(string item) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public int IndexOf(string item)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// CopyTo(string[] array, int arrayIndex) method not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void CopyTo(string[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// IsReadOnly not supported.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool IsReadOnly
        {
            get { throw new NotImplementedException(); }
        }
        #endregion

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~StringArrayNoLOH()
        {
            Dispose(false);
        }

        private bool _disposed = false;

        private void Dispose(bool disposing)
        {
            if (!this._disposed)
            {
                if (disposing)
                {
                    //managed here...
                }

                //unmanaged here...

                Free();
            }
            this._disposed = true;
        }

        #endregion
    }

    #endregion


    #region SplitNoLOH and SplitLazyLoad (StringExtensionMethods)

    public static class StringExtensionMethods
    {
        #region SplitNoLOH

        /// <summary>
        /// Use this method in place of the built-in Split method to avoid using the GC Large Object Heap (LOH).
        /// 
        /// Important: This method is intended to be used ONLY for splitting very large strings
        /// (over approx. 21,250 characters). The string itself goes on the LOH when its over approx.
        /// 42,500 characters (since the .NET char is 2 bytes for unicode). However, the internal
        /// implementation code for the regular .NET string Split method utilizes an int[] the length
        /// of the string to keep track of the split delimiter locations, which ends up going on the
        /// LOH when it's over roughly int[21250] (since the .NET int is 4 bytes).
        /// 
        /// Important: EnumerationBehavior is required to specify whether to automatically Dispose()
        ///            after enumeration is complete for cases like the following:
        ///               foreach(string item in text.SplitNoLOH('^'))
        ///               {
        ///               }
        ///            In this case the invisible StringArrayNoLOH instance that's returned from
        ///            text.SplitNoLOH('^') will not have Dispose() called automatically after the
        ///            foreach loop ends, and will leak unmanaged memory in StringArrayNoLOH.
        ///            On the other hand, when used as follows, the using block automatically
        ///            calls Dispose() on StringArrayNoLOH when exits the using block, and will
        ///            NOT leak unmanaged memory in StringArrayNoLOH:
        ///               using (StringArrayNoLOH array = text.SplitNoLOH('^', EnumerationBehavior.DoNotDisposeAfterEnumerated))
        ///               {
        ///                   foreach (string item in array)
        ///                   {
        ///                   }
        ///                   foreach (string item in array)
        ///                   {
        ///                   }
        ///               }
        /// </summary>
        public static StringArrayNoLOH SplitNoLOH(this string value, char delimiter)
        {
            return SplitNoLOH(value, delimiter, EnumerationBehavior.DisposeAfterEnumerated);
        }
        public static StringArrayNoLOH SplitNoLOH(this string value, char delimiter, EnumerationBehavior enumerationBehavior)
        {
            if (string.IsNullOrEmpty(value))
                return new StringArrayNoLOH();

            StringArrayNoLOH array = new StringArrayNoLOH(value, enumerationBehavior);
            using (IEnumerator<Tuple<int, int>> enumerator = new DelimiterOffsetEnumerator(ref value, delimiter))
            {
                while (enumerator.MoveNext())
                {
                    array.AddOffset(enumerator.Current);
                }
            }
            return array;
        }
        private class DelimiterOffsetEnumerator : IEnumerator<Tuple<int, int>>
        {
            private string _data = string.Empty;
            private char _delimiter;
            private int _position = 0;
            private Tuple<int, int> _current = new Tuple<int, int>(0, 0);

            public DelimiterOffsetEnumerator(ref string data, char delimiter)
            {
                _data = data;
                _delimiter = delimiter;
            }
            private DelimiterOffsetEnumerator()
            {
            }

            public void Dispose()
            {
            }

            public Tuple<int, int> Current
            {
                get { return _current; }
            }

            object IEnumerator.Current
            {
                get { return _current; }
            }

            Tuple<int, int> IEnumerator<Tuple<int, int>>.Current
            {
                get { return _current; }
            }

            public bool MoveNext()
            {
                //get index of next delimiter in source string on each enumeration
                int index = _data.IndexOf(_delimiter, _position);
                if (index == -1)
                {
                    if (_position < _data.Length)
                    {
                        //get to end of string
                        _current = new Tuple<int, int>(_position, (_data.Length - _position));
                        _position = _data.Length;
                        return true;
                    }
                    else
                        return false;
                }
                else
                {
                    //stores position of delimiter and length of substring in Tuple<int,int>
                    //that gets added to StringArrayNoLOH on each enumeration and is used for
                    //indexer implementation inside that StringArrayNoLOH when it's returned
                    _current = new Tuple<int, int>(_position, (index - _position));
                    _position = index + 1;
                    return true;
                }
            }

            public void Reset()
            {
                _position = 0;
                _current = new Tuple<int, int>(0, 0);
            }
        }

        #endregion

        #region SplitLazyLoad

        /// <summary>
        /// Use this method in place of the built-in Split to avoid using the GC Large Object Heap (LOH).
        /// It splits the source string in-place by simply enumerating it. It does not return a string
        /// array. This is by far the fastest, most efficient method of splitting a string. The main
        /// draw back is it doesn't return a string array object, so it can't be used for code that
        /// needs to be able to access a returned string array by index. It can only be used inside
        /// an enumeration loop.
        /// </summary>
        public static IEnumerable<string> SplitLazyLoad(this string value, char delimiter)
        {
            using (IEnumerator<string> enumerator = new CustomStringEnumerator(ref value, delimiter))
            {
                while (enumerator.MoveNext())
                {
                    yield return enumerator.Current;
                }
            }
        }
        private class CustomStringEnumerator : IEnumerator<string>
        {
            private string _data = string.Empty;
            private string _current = string.Empty;
            private char _delimiter;
            private int _position = 0;

            public CustomStringEnumerator(ref string data, char delimiter)
            {
                _data = data;
                _delimiter = delimiter;
            }
            private CustomStringEnumerator()
            {
            }

            public string Current
            {
                get { return _current; }
            }

            public void Dispose()
            {
            }

            object IEnumerator.Current
            {
                get { return _current; }
            }

            public bool MoveNext()
            {
                //get index of next delimiter in source string on each enumeration
                int index = _data.IndexOf(_delimiter, _position);
                if (index == -1)
                {
                    if (_position < _data.Length)
                    {
                        //get to end of string
                        _current = _data.Substring(_position, _data.Length - _position);
                        _position = _data.Length;
                        return true;
                    }
                    else
                        return false;
                }
                else
                {
                    //get next substring after delimiter from source string on each enumeration
                    _current = _data.Substring(_position, index - _position);
                    _position = index + 1;
                    return true;
                }
            }

            public void Reset()
            {
                _position = 0;
                _current = string.Empty;
            }
        }

        #endregion
    }

    #endregion

    
    #region Helpers

    /// <summary>
    /// Helper that simply gets the size of the byte buffer that will
    /// be needed to binary serialize a collection/list of objects. Just
    /// gets the required size, doesn't actually create or use any buffer.
    ///
    /// How to use:
    ///
    ///    BinaryFormatter bf = new BinaryFormatter();
    ///    SizeOfSerializationStream s = new SizeOfSerializationStream();
    ///    bf.Serialize(s, list);
    ///    long size = s.Length;
    /// </summary>
    internal class SizeOfSerializationStream : Stream
    {
        private long _size = 0;

        public override void Write(byte[] buffer, int offset, int count)
        {
            _size += count;
        }
        public override long Length
        {
            get { return _size; }
        }
        public void Reset()
        {
            _size = 0;
        }

        public override bool CanWrite
        {
            get { return true; }
        }
        public override bool CanRead
        {
            get { return false; }
        }
        public override bool CanSeek
        {
            get { return false; }
        }
        public override void Flush()
        {
        }
        public override long Position
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }
        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }
    }

    #endregion
}
