//! 防串窗与防迟到文本的三重栅栏（`Epoch + Generation + TargetRevision`）及译文 FIFO 目标绑定队列。

use std::collections::VecDeque;

/// 单个连接客户端的唯一标识。
pub type PeerId = u64;

/// 绑定的前台可编辑输入框身份。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TargetBinding {
    pub peer_id: PeerId,
    pub generation: u64,
}

/// 文本帧出队写管道前的二次资格核验快照。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DeliveryFence {
    pub epoch: u64,
    pub target: TargetBinding,
    pub target_revision: u64,
}

impl DeliveryFence {
    /// 校验出队时刻是否仍与捕获时刻处于同一轮录音、同一目标、同一绑定版本。
    pub fn matches(
        &self,
        current_epoch: u64,
        current_target: Option<TargetBinding>,
        current_revision: u64,
    ) -> bool {
        self.epoch == current_epoch
            && self.target_revision == current_revision
            && current_target == Some(self.target)
    }
}

/// 翻译模式专用的句首目标捕获与 FIFO 消费队列（1:1 对齐 `VoiceImeSharedHost.SourceRecognized`）。
///
/// 规则：
/// 1. 每句首个非空原文片段（partial 或无 partial 时的 final）锁定当时的 `(Option<TargetBinding>, revision)`；
/// 2. 若说半句话期间用户切换了输入框，不会把整句重绑到新输入框；
/// 3. 非空原文 final 将锁定的槽位（含 `None` 失效占位）推入 FIFO；
/// 4. 译文 final 严格按 FIFO 弹出对应槽位，仅当目标与 `revision` 均未改变时才允许投递。
#[derive(Debug, Default)]
pub struct TranslationTargetQueue {
    sentence_started: bool,
    current_target: Option<TargetBinding>,
    current_revision: u64,
    fifo: VecDeque<(Option<TargetBinding>, u64)>,
}

impl TranslationTargetQueue {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn clear(&mut self) {
        self.sentence_started = false;
        self.current_target = None;
        self.current_revision = 0;
        self.fifo.clear();
    }

    /// 处理 ASR 原文快照（含 partial 与 final）。
    pub fn on_source(
        &mut self,
        text: &str,
        is_final: bool,
        active_target: Option<TargetBinding>,
        active_revision: u64,
    ) {
        let non_empty = !text.trim().is_empty();
        if non_empty && !self.sentence_started {
            self.sentence_started = true;
            self.current_target = active_target;
            self.current_revision = active_revision;
        }
        if is_final {
            if non_empty {
                self.fifo.push_back((self.current_target, self.current_revision));
            }
            self.sentence_started = false;
            self.current_target = None;
        }
    }

    /// 消费下一条定稿译文对应的句首目标；仅当目标未发生切换（包括 A→B→A 往返也被 `revision` 拦截）时返回 `Some`。
    pub fn pop_valid_destination(
        &mut self,
        active_target: Option<TargetBinding>,
        active_revision: u64,
    ) -> Option<TargetBinding> {
        let (captured_target, captured_revision) = self.fifo.pop_front()?;
        let destination = captured_target?;
        if captured_revision == active_revision && active_target == Some(destination) {
            Some(destination)
        } else {
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn translation_fifo_captures_sentence_start_and_blocks_aba_switch() {
        let mut queue = TranslationTargetQueue::new();
        let target_a = TargetBinding {
            peer_id: 1,
            generation: 10,
        };
        // 第 1 句在目标 A 开始，中途目标切换到 None 再切回 A（revision 从 1 变为 3）
        queue.on_source("你好", false, Some(target_a), 1);
        queue.on_source("你好世界", true, None, 2);

        // 第 2 句在切回后的目标 A (revision=3) 完整说完
        queue.on_source("第二句", true, Some(target_a), 3);

        // 第 1 句因经历了 A -> None -> A，revision 不匹配，必须被拦截丢弃，但占位被正确消费
        assert_eq!(queue.pop_valid_destination(Some(target_a), 3), None);
        // 第 2 句正常对齐并投递到目标 A
        assert_eq!(
            queue.pop_valid_destination(Some(target_a), 3),
            Some(target_a)
        );
    }
}
