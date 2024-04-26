// MIT License (MIT) - Copyright (c) 2014 jakevn - Please see included LICENSE file
namespace Massive.Examples.NetAdvanced
{
    public interface IEquipItem
    {
        bool Equipped { get; set; }

        string MountPoint { get; set; }
    }
}