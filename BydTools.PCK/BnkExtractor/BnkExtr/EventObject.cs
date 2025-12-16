using System.Collections.Generic;

namespace BnkExtractor.BnkExtr
{
    public class EventObject
    {
        public uint action_count;
        public List<uint> action_ids = new List<uint>();
    }
}
