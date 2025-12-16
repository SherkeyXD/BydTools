namespace BnkExtractor.BnkExtr
{
    public class EventActionObject
    {
        public EventActionScope scope;
        public EventActionType action_type;
        public uint game_object_id;
        public byte parameter_count;
        public List<EventActionParameterType> parameters_types =
            new List<EventActionParameterType>();
        public List<sbyte> parameters = new List<sbyte>();
    }
}
