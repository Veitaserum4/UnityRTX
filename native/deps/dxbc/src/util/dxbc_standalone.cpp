// Standalone dxbc: DXVK defines its logger instance per DLL; here, once.
#include "log/log.h"
namespace dxvk {
  Logger Logger::s_instance("dxbc.log");
}
