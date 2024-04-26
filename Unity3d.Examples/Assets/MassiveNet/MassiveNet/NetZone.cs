// MIT License (MIT) - Copyright (c) 2014 jakevn - Please see included LICENSE file
using System.Net;
using UnityEngine;

namespace MassiveNet {
    /// <summary>
    /// Contains configuration parameters for Zones.
    /// 包含区域的配置参数。
    /// </summary>
    public class NetZone {
        public uint Id { get; internal set; }

        public bool Available { get; internal set; }

        public bool Assigned { get; internal set; }

        public IPEndPoint ServerEndpoint { get; internal set; }

        public string PublicEndpoint { get; internal set; }

        public NetConnection Server { get; internal set; }

        public Vector3 Position { get; internal set; }

        public int ViewIdMin { get; internal set; }

        public int ViewIdMax { get; internal set; }

        /// <summary>
        /// A NetConnection will be forced to handover to this Zone when within this range.
        /// 在此范围内时，NetConnection 将被迫切换到该区域。
        /// </summary>
        public int HandoverMinDistance = 100;

        /// <summary>
        /// The maximum distance from center a NetConnection can be before forcing handoff to another zone or disconnect.
        /// 在强制切换到另一个区域或断开连接之前，网络连接距中心的最大距离。
        /// </summary>
        public int HandoverMaxDistance = 400;

        /// <summary>
        /// The radius of the zone. Under ideal conditions, this zone will only control NetConnections within this range.
        /// 区域的半径。 理想情况下，该区域只会控制该范围内的网络连接。
        /// </summary>
        public int ZoneSize = 300;

        /// <summary>
        /// How close a NetConnection must be to this Zone center before this Zone takes control.
        /// 在该区域取得控制权之前，NetConnection 与该区域中心的距离必须有多近。
        /// </summary>
        public int HandoverDistance = 200;

        internal void RemoveServer() {
            Server = null;
            Assigned = false;
        }

        internal bool InRange(Vector3 position) {
            return Vector3.Distance(Position, position) < ZoneSize;
        }

        internal bool InRangeMax(Vector3 position) {
            return Vector3.Distance(Position, position) < HandoverMaxDistance;
        }

        internal float Distance(Vector3 position) {
            return Vector3.Distance(Position, position);
        }

        /// <summary>
        /// Creates a Zone using the default size and handoff ranges.
        /// 使用默认大小和切换范围创建区域。
        /// </summary>
        internal NetZone(Vector3 position, int viewIdMin, int viewIdMax) {
            Id = NetMath.RandomUint();
            ViewIdMin = viewIdMin;
            ViewIdMax = viewIdMax;
            Position = position;
        }

        /// <summary>
        /// Creates a Zone using custom size and handoff ranges.
        /// 使用自定义大小和切换范围创建区域。
        /// </summary>
        internal NetZone(Vector3 position, int viewIdMin, int viewIdMax, int handoff, int handoffMin, int handoffMax, int zoneSize) {
            Id = NetMath.RandomUint();
            ViewIdMin = viewIdMin;
            ViewIdMax = viewIdMax;
            Position = position;
            HandoverDistance = handoff;
            HandoverMinDistance = handoffMin;
            HandoverMaxDistance = handoffMax;
            ZoneSize = zoneSize;
        }

        /// <summary>
        /// Constructor used for deserialization. 
        /// 用于反序列化的构造函数。
        /// </summary>
        internal NetZone() {}
    }
}