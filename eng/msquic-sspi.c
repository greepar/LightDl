// Resolve the extended SSPI function through the OS-provided function table.
// The preview's import library incorrectly required a Secur32.dll named export
// that is absent on the tested Windows runners. Preserve the OS implementation
// and its buffer-size validation instead of substituting the older 3-arg API.
#define SECURITY_WIN32
#define _WIN32_WINNT 0x0A00
#define NTDDI_VERSION 0x0A000000
#include <windows.h>
#include <security.h>

SECURITY_STATUS WINAPI QueryContextAttributesExW(
    PCtxtHandle context, unsigned long attribute, void *buffer, unsigned long size)
{
    PSecurityFunctionTableW table = InitSecurityInterfaceW();
    if (table == NULL || table->dwVersion < SECURITY_SUPPORT_PROVIDER_INTERFACE_VERSION_4 ||
        table->QueryContextAttributesExW == NULL)
        return SEC_E_UNSUPPORTED_FUNCTION;
    return table->QueryContextAttributesExW(context, attribute, buffer, size);
}
