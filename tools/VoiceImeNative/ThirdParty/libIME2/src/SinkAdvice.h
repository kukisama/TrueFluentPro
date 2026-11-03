#pragma once
// TrueFluent 2026-11-03: expose advice failures and safely transfer one subscription.

#include <msctf.h>
#include "ComPtr.h"

namespace Ime {

class SinkAdvice {
public:
    SinkAdvice() : cookie_{ TF_INVALID_COOKIE }, result_{ E_NOINTERFACE } {}

    SinkAdvice(ITfSource* source, REFIID riid, IUnknown* sink) : SinkAdvice() {
        DWORD cookie = TF_INVALID_COOKIE;
        result_ = source ? source->AdviseSink(riid, sink, &cookie) : E_POINTER;
        if (result_ == S_OK) {
            source_ = source;
            cookie_ = cookie;
        }
        else if (SUCCEEDED(result_)) result_ = E_FAIL;
    }

    SinkAdvice(const SinkAdvice&) = delete;

    SinkAdvice(SinkAdvice&& other) noexcept : SinkAdvice() { *this = std::move(other); }

    ~SinkAdvice() {
        unadvise();
    }

    SinkAdvice& operator = (SinkAdvice&& other) noexcept {
        if (this != &other) {
            unadvise();
            source_ = std::move(other.source_);
            cookie_ = std::exchange(other.cookie_, TF_INVALID_COOKIE);
            result_ = other.result_;
        }
        return *this;
    }

    HRESULT result() const { return result_; }

    void unadvise() {
        if (source_ && cookie_ != TF_INVALID_COOKIE) {
            (source_->UnadviseSink)(cookie_);
            source_ = nullptr;
            cookie_ = TF_INVALID_COOKIE;
        }
    }

private:
    ComPtr<ITfSource> source_;
    DWORD cookie_;
    HRESULT result_;
};

} // namespace Ime
