using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace DisplayWeave
{
    // Settings files other than the layout: read without DTDs or external entities, and replaced atomically.
    internal static class XmlFile
    {
        // null when the file does not exist; throws when it cannot be read as saved.
        internal static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            XmlReaderSettings options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 1024 * 1024, MaxCharactersFromEntities = 0 };
            using (XmlReader reader = XmlReader.Create(path, options))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }

        internal static void Write(string path, object value)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Written through to disk before it replaces the old file, so a power cut cannot leave an empty file.
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
                        new XmlSerializer(value.GetType()).Serialize(writer, value);
                    stream.Flush(true);
                }
                if (File.Exists(full)) File.Replace(temporary, full, null);
                else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
