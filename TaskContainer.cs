using System.Threading.Tasks;
using SADBSConveyorLib;

namespace SADBSConveyor
{
    class TaskContainer
    {
        public int TaskNumber { get; set; }
        public Task Task { get; set; }
        public Logging Log { get; set; }
    }
}
