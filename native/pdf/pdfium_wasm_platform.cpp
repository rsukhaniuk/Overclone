// Standalone WASM port for PDFium6688c576. The worker has no system font
// filesystem. PDFium's embedded fonts and document-embedded fonts remain in
// use; nonembedded external-font substitution is explicitly not ink proof.
#include "core/fxge/cfx_gemodule.h"
#include "core/fxge/systemfontinfo_iface.h"
#include <memory>
namespace {
class E1WasmPlatform final : public CFX_GEModule::PlatformIface {
 public:
  void Init() override {}
  void Terminate() override {}
  std::unique_ptr<SystemFontInfoIface> CreateDefaultSystemFontInfo() override { return nullptr; }
};
}
std::unique_ptr<CFX_GEModule::PlatformIface> CFX_GEModule::PlatformIface::Create() {
 return std::make_unique<E1WasmPlatform>();
}
