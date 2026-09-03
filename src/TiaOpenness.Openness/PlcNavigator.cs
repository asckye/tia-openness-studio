using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Openness
{
    /// <summary>
    /// One PLC's software, plus flat lookups keyed by the slash-separated path the wire
    /// protocol uses. The Openness object model is a tree of compositions with no addressing
    /// scheme of its own, so every call would otherwise re-walk it.
    /// </summary>
    internal sealed class PlcContext
    {
        public string DeviceId;
        public PlcSoftware Software;

        public readonly Dictionary<string, PlcBlock> Blocks =
            new Dictionary<string, PlcBlock>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, PlcType> Types =
            new Dictionary<string, PlcType>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, PlcTagTable> TagTables =
            new Dictionary<string, PlcTagTable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Group that owns each block, so an export can recreate the folder layout.</summary>
        public readonly Dictionary<string, string> Folders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Walks the project tree and turns Openness objects into wire DTOs.</summary>
    internal static class PlcNavigator
    {
        // ---- devices -------------------------------------------------------

        /// <summary>Every device in the project, including those nested in device groups.</summary>
        public static IEnumerable<Device> AllDevices(Project project)
        {
            foreach (var device in project.Devices.SafeEnumerate()) yield return device;

            foreach (var group in project.DeviceGroups.SafeEnumerate())
            {
                foreach (var device in AllDevices(group)) yield return device;
            }
        }

        private static IEnumerable<Device> AllDevices(DeviceUserGroup group)
        {
            foreach (var device in group.Devices.SafeEnumerate()) yield return device;

            foreach (var child in group.Groups.SafeEnumerate())
            {
                foreach (var device in AllDevices(child)) yield return device;
            }
        }

        public static DeviceInfo Describe(Device device)
        {
            var software = FindSoftware(device);
            var carrier = FindSoftwareCarrier(device) ?? device.DeviceItems.SafeEnumerate().FirstOrDefault();

            return new DeviceInfo
            {
                Id = device.Name,
                Name = device.Name,
                TypeIdentifier = device.TypeIdentifier,
                ArticleNumber = carrier?.Attr<string>("OrderNumber") ?? device.Attr<string>("OrderNumber"),
                FirmwareVersion = carrier?.Attr<string>("FirmwareVersion"),
                Category = Categorize(software),
                ItemNames = device.DeviceItems.SafeEnumerate().Select(i => i.Name).ToList(),
            };
        }

        /// <summary>
        /// Decided from the software type name rather than a typed check, so the adapter needs
        /// only Siemens.Engineering.dll &#8212; HmiTarget lives in Siemens.Engineering.Hmi.dll.
        /// </summary>
        private static string Categorize(Software software)
        {
            if (software == null) return "Other";
            if (software is PlcSoftware) return "Plc";

            var name = software.GetType().Name;
            if (name.IndexOf("Hmi", StringComparison.OrdinalIgnoreCase) >= 0) return "Hmi";
            if (name.IndexOf("Drive", StringComparison.OrdinalIgnoreCase) >= 0) return "Drive";
            return "Other";
        }

        /// <summary>Finds the PLC software behind a device, descending into nested device items.</summary>
        public static PlcSoftware FindPlcSoftware(Device device)
        {
            return FindSoftware(device) as PlcSoftware;
        }

        private static Software FindSoftware(Device device)
        {
            foreach (var item in AllDeviceItems(device))
            {
                var container = item.GetService<SoftwareContainer>();
                if (container?.Software != null) return container.Software;
            }
            return null;
        }

        private static DeviceItem FindSoftwareCarrier(Device device)
        {
            foreach (var item in AllDeviceItems(device))
            {
                if (item.GetService<SoftwareContainer>()?.Software != null) return item;
            }
            return null;
        }

        public static IEnumerable<DeviceItem> AllDeviceItems(Device device)
        {
            foreach (var item in device.DeviceItems.SafeEnumerate())
            {
                yield return item;
                foreach (var nested in AllDeviceItems(item)) yield return nested;
            }
        }

        private static IEnumerable<DeviceItem> AllDeviceItems(DeviceItem item)
        {
            foreach (var child in item.DeviceItems.SafeEnumerate())
            {
                yield return child;
                foreach (var nested in AllDeviceItems(child)) yield return nested;
            }
        }

        // ---- blocks --------------------------------------------------------

        /// <summary>Fills <paramref name="context"/> with every block, UDT and tag table.</summary>
        public static void Index(PlcContext context)
        {
            context.Blocks.Clear();
            context.Types.Clear();
            context.TagTables.Clear();
            context.Folders.Clear();

            IndexBlockGroup(context, context.Software.BlockGroup, string.Empty);
            IndexTypeGroup(context, context.Software.TypeGroup, string.Empty);
            IndexTagTableGroup(context, context.Software.TagTableGroup, string.Empty);
        }

        private static void IndexBlockGroup(PlcContext context, PlcBlockGroup group, string prefix)
        {
            foreach (var block in group.Blocks.SafeEnumerate())
            {
                var path = Join(prefix, block.Name);
                context.Blocks[path] = block;
                context.Folders[path] = prefix;
            }

            foreach (var child in group.Groups.SafeEnumerate())
            {
                IndexBlockGroup(context, child, Join(prefix, child.Name));
            }
        }

        private static void IndexTypeGroup(PlcContext context, PlcTypeGroup group, string prefix)
        {
            foreach (var type in group.Types.SafeEnumerate())
            {
                var path = Join(prefix, type.Name);
                context.Types[path] = type;
                context.Folders[path] = prefix;
            }

            foreach (var child in group.Groups.SafeEnumerate())
            {
                IndexTypeGroup(context, child, Join(prefix, child.Name));
            }
        }

        private static void IndexTagTableGroup(PlcContext context, PlcTagTableGroup group, string prefix)
        {
            foreach (var table in group.TagTables.SafeEnumerate())
            {
                var path = Join(prefix, table.Name);
                context.TagTables[path] = table;
                context.Folders[path] = prefix;
            }

            foreach (var child in group.Groups.SafeEnumerate())
            {
                IndexTagTableGroup(context, child, Join(prefix, child.Name));
            }
        }

        public static BlockInfo Describe(PlcBlock block, string path)
        {
            var db = block as DataBlock;
            var instanceOf = db?.Prop<string>("InstanceOfName");

            return new BlockInfo
            {
                Path = path,
                Name = block.Name,
                Kind = KindOf(block, instanceOf),
                Number = block.Prop<int?>("Number"),
                ProgrammingLanguage = block.Prop<string>("ProgrammingLanguage"),
                IsConsistent = block.Prop("IsConsistent", true),
                IsKnowHowProtected = block.Prop("IsKnowHowProtected", false),
                ModifiedDate = block.Prop<DateTime>("ModifiedDate").AsOffset(),
                HeaderAuthor = block.Attr<string>("HeaderAuthor"),
                HeaderVersion = block.Attr<string>("HeaderVersion"),
            };
        }

        public static BlockInfo Describe(PlcType type, string path)
        {
            return new BlockInfo
            {
                Path = path,
                Name = type.Name,
                Kind = BlockKind.UDT,
                Number = null,
                ProgrammingLanguage = null,
                IsConsistent = type.Prop("IsConsistent", true),
                IsKnowHowProtected = type.Prop("IsKnowHowProtected", false),
                ModifiedDate = type.Prop<DateTime>("ModifiedDate").AsOffset(),
                HeaderAuthor = type.Attr<string>("HeaderAuthor"),
                HeaderVersion = type.Attr<string>("HeaderVersion"),
            };
        }

        private static BlockKind KindOf(PlcBlock block, string instanceOfName)
        {
            if (block is OB) return BlockKind.OB;
            if (block is FB) return BlockKind.FB;
            if (block is FC) return BlockKind.FC;
            if (block is DataBlock)
            {
                return string.IsNullOrWhiteSpace(instanceOfName) ? BlockKind.DB : BlockKind.InstanceDB;
            }
            return BlockKind.Unknown;
        }

        // ---- tags ----------------------------------------------------------

        public static TagTableInfo Describe(PlcTagTable table, string path)
        {
            var count = 0;
            try { count = table.Tags.Count; } catch (Exception) { count = 0; }

            return new TagTableInfo
            {
                Path = path,
                Name = table.Name,
                TagCount = count,
                IsDefault = string.Equals(table.Name, "Default tag table", StringComparison.OrdinalIgnoreCase),
            };
        }

        public static TagInfo Describe(PlcTag tag, string tableName)
        {
            return new TagInfo
            {
                Name = tag.Name,
                DataType = tag.Prop<string>("DataTypeName"),
                LogicalAddress = tag.Prop<string>("LogicalAddress"),
                Comment = (tag.Prop<object>("Comment") as MultilingualText).AsText(),
                TableName = tableName,
            };
        }

        // ---- helpers -------------------------------------------------------

        public static string Join(string prefix, string name)
        {
            return string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
        }
    }
}
