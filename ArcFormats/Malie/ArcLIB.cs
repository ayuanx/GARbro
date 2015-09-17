﻿//! \file       ArcLIB.cs
//! \date       Thu Jun 25 06:46:51 2015
//! \brief      Malie System archive implementation.
//
// Copyright (C) 2015 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using GameRes.Encryption;
using GameRes.Utility;

namespace GameRes.Formats.Malie
{
    [Export(typeof(ArchiveFormat))]
    public class LibOpener : ArchiveFormat
    {
        public override string         Tag { get { return "LIB"; } }
        public override string Description { get { return "Malie engine resource archive"; } }
        public override uint     Signature { get { return 0x0042494C; } } // 'LIB'
        public override bool  IsHierarchic { get { return true; } }
        public override bool     CanCreate { get { return false; } }

        public override ArcFile TryOpen (ArcView file)
        {
            var reader = new Reader (file);
            if (reader.ReadIndex ("", 0, (uint)file.MaxOffset))
                return new ArcFile (file, this, reader.Dir);
            else
                return null;
        }

        internal class Reader
        {
            ArcView.Frame   m_view;
            List<Entry>     m_dir = new List<Entry>();

            public List<Entry> Dir { get { return m_dir; } }

            public Reader (ArcView file)
            {
                m_view = file.View;
            }

            public bool ReadIndex (string root, long base_offset, uint size)
            {
                uint signature = m_view.ReadUInt32 (base_offset);
                if (0x0042494C != signature)
                    return false;

                int count = m_view.ReadInt16 (base_offset + 8);
                if (count <= 0)
                    return false;
                long index_offset = base_offset + 0x10;
                uint index_size = (uint)(0x30 * count);
                if (index_size > size)
                    return false;
                if (index_size > m_view.Reserve (index_offset, index_size))
                    return false;
                long data_offset = index_offset + index_size;
                if (m_dir.Capacity < m_dir.Count + count)
                    m_dir.Capacity = m_dir.Count + count;
                for (int i = 0; i < count; ++i)
                {
                    string name = m_view.ReadString (index_offset, 0x24);
                    uint entry_size = m_view.ReadUInt32 (index_offset+0x24);
                    long offset = base_offset + m_view.ReadUInt32 (index_offset+0x28);
                    index_offset += 0x30;
                    string ext = Path.GetExtension (name);
                    name = Path.Combine (root, name);
                    if (string.IsNullOrEmpty (ext) && ReadIndex (name, offset, entry_size))
                    {
                        continue;
                    }
                    if (offset < data_offset || offset + entry_size > base_offset + size)
                        return false;

                    var entry = FormatCatalog.Instance.Create<Entry> (name);
                    entry.Offset = offset;
                    entry.Size   = entry_size;
                    m_dir.Add (entry);
                }
                return true;
            }
        }
    }

    public class MalieArchive : ArcFile
    {
        public readonly Camellia Encryption;

        public MalieArchive (ArcView file, ArchiveFormat format, ICollection<Entry> dir, Camellia encryption)
            : base (file, format, dir)
        {
            Encryption = encryption;
        }
    }

    [Serializable]
    public class LibScheme : ResourceScheme
    {
        public Dictionary<string, uint[]> KnownKeys;
    }

    [Export(typeof(ArchiveFormat))]
    public class DatOpener : ArchiveFormat
    {
        public override string         Tag { get { return "LIBP"; } }
        public override string Description { get { return "Malie engine encrypted archive"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return true; } }
        public override bool     CanCreate { get { return false; } }

        public DatOpener ()
        {
            Extensions = new string[] { "dat" };
        }

        public override ArcFile TryOpen (ArcView file)
        {
            var reader = new Reader (file);
            foreach (var key in KnownKeys.Values)
            {
                var encryption = new Camellia (key);
                if (reader.ReadIndex (encryption))
                    return new MalieArchive (file, this, reader.Dir, encryption);
            }
            return null;
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            var march = arc as MalieArchive;
            if (null == march)
                return arc.File.CreateStream (entry.Offset, entry.Size);
            var data = new byte[entry.Size];
            ReadEncrypted (arc.File.View, march.Encryption, entry.Offset, data, 0, (int)entry.Size);
            return new MemoryStream (data);
        }

        internal class Reader
        {
            ArcView.Frame   m_view;
            readonly long   m_max_offset;
            long            m_base_offset;
            Camellia        m_enc;
            List<Entry>     m_dir = new List<Entry>();

            public List<Entry> Dir { get { return m_dir; } }

            public Reader (ArcView file)
            {
                m_view = file.View;
                m_max_offset = file.MaxOffset;
            }

            byte[] m_header = new byte[0x10];
            byte[] m_index;
            uint[] m_offset_table;

            public bool ReadIndex (Camellia encryption)
            {
                m_base_offset = 0;
                m_enc = encryption;

                if (0x10 != ReadEncrypted (m_view, m_enc, m_base_offset, m_header, 0, 0x10))
                    return false;
                if (!Binary.AsciiEqual (m_header, 0, "LIBP"))
                    return false;

                int count = LittleEndian.ToInt32 (m_header, 4);
                if (count <= 0)
                    return false;
                int offset_count = LittleEndian.ToInt32 (m_header, 8);

                m_index     = new byte[0x20 * count];
                var offsets = new byte[4 * offset_count];

                m_base_offset += 0x10;
                if (m_index.Length != ReadEncrypted (m_view, m_enc, m_base_offset, m_index, 0, m_index.Length))
                    return false;
                m_base_offset += m_index.Length;
                if (offsets.Length != ReadEncrypted (m_view, m_enc, m_base_offset, offsets, 0, offsets.Length))
                    return false;
                m_offset_table = new uint[offset_count];
                Buffer.BlockCopy (offsets, 0, m_offset_table, 0, offsets.Length);

                m_base_offset += offsets.Length;
                m_base_offset = (m_base_offset + 0xFFF) & ~0xFFF;

                m_dir.Capacity = offset_count;
                ReadDir ("", 0, 1);
                return m_dir.Count > 0;
            }

            private void ReadDir (string root, int entry_index, int count)
            {
                int current_offset = entry_index * 0x20;
                for (int i = 0; i < count; ++i)
                {
                    string name = Binary.GetCString (m_index, current_offset, 0x14);
                    int flags   = LittleEndian.ToInt32 (m_index, current_offset+0x14);
                    int offset  = LittleEndian.ToInt32 (m_index, current_offset+0x18);
                    uint size   = LittleEndian.ToUInt32 (m_index, current_offset+0x1c);
                    current_offset += 0x20;
                    name = Path.Combine (root, name);
                    if (0 == (flags & 0x10000))
                    {
                        if (offset > entry_index)
                            ReadDir (name, (int)offset, (int)size);
                        continue;
                    }
                    long entry_offset = m_base_offset + ((long)m_offset_table[offset] << 10);
                    var entry = FormatCatalog.Instance.Create<Entry> (name);
                    if (entry.CheckPlacement (m_max_offset))
                    {
                        entry.Offset = entry_offset;
                        entry.Size   = size;
                        m_dir.Add (entry);
                    }
                }
            }
        }

        private static int ReadEncrypted (ArcView.Frame view, Camellia enc, long offset, byte[] buffer, int index, int length)
        {
            int offset_pad  = (int)offset & 0xF;
            int aligned_len = (offset_pad + length + 0xF) & ~0xF;    
            byte[] aligned_buf;
            int block = 0;
            if (aligned_len == length)
            {
                aligned_buf = buffer;
                block = index;
            }
            else
            {
                aligned_buf = new byte[aligned_len];
            }

            int read = view.Read (offset - offset_pad, aligned_buf, block, (uint)aligned_len);
            if (read < offset_pad)
                return 0;

            for (int block_count = aligned_len / 0x10; block_count > 0; --block_count)
            {
                enc.DecryptBlock (offset, aligned_buf, block);
                block  += 0x10;
                offset += 0x10;
            }
            if (aligned_buf != buffer)
                Buffer.BlockCopy (aligned_buf, offset_pad, buffer, index, length);
            return Math.Min (length, read-offset_pad);
        }

        public static Dictionary<string, uint[]> KnownKeys = new Dictionary<string, uint[]>();

        public override ResourceScheme Scheme
        {
            get { return new LibScheme { KnownKeys = KnownKeys }; }
            set { KnownKeys = ((LibScheme)value).KnownKeys; }
        }
    }
}
