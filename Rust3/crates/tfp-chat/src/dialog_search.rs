//! Dialog search engine — case-insensitive full-text search across chat messages.

/// Which field a match was found in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SearchField {
    Text,
    Reasoning,
}

/// A single search match location.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SearchMatch {
    pub message_index: usize,
    pub start: usize,
    pub length: usize,
    pub field: SearchField,
}

/// Case-insensitive dialog search engine with circular navigation.
pub struct DialogSearchEngine {
    matches: Vec<SearchMatch>,
    current_index: usize,
}

impl DialogSearchEngine {
    /// Create a new empty search engine.
    pub fn new() -> Self {
        Self {
            matches: Vec::new(),
            current_index: 0,
        }
    }

    /// Execute a case-insensitive search across messages.
    ///
    /// `messages` is a slice of `(text, reasoning_text)` tuples.
    /// Returns the total number of matches found.
    pub fn search(&mut self, messages: &[(String, String)], query: &str) -> usize {
        self.matches.clear();
        self.current_index = 0;

        if query.is_empty() {
            return 0;
        }

        let query_lower = query.to_lowercase();

        for (idx, (text, reasoning)) in messages.iter().enumerate() {
            // Search in text field
            let text_lower = text.to_lowercase();
            let mut search_from = 0;
            while let Some(pos) = text_lower[search_from..].find(&query_lower) {
                self.matches.push(SearchMatch {
                    message_index: idx,
                    start: search_from + pos,
                    length: query.len(),
                    field: SearchField::Text,
                });
                search_from += pos + 1;
            }

            // Search in reasoning field
            let reasoning_lower = reasoning.to_lowercase();
            let mut search_from = 0;
            while let Some(pos) = reasoning_lower[search_from..].find(&query_lower) {
                self.matches.push(SearchMatch {
                    message_index: idx,
                    start: search_from + pos,
                    length: query.len(),
                    field: SearchField::Reasoning,
                });
                search_from += pos + 1;
            }
        }

        self.matches.len()
    }

    /// Navigate to the next match (wraps around).
    pub fn navigate_next(&mut self) -> Option<&SearchMatch> {
        if self.matches.is_empty() {
            return None;
        }
        self.current_index = (self.current_index + 1) % self.matches.len();
        Some(&self.matches[self.current_index])
    }

    /// Navigate to the previous match (wraps around).
    pub fn navigate_previous(&mut self) -> Option<&SearchMatch> {
        if self.matches.is_empty() {
            return None;
        }
        if self.current_index == 0 {
            self.current_index = self.matches.len() - 1;
        } else {
            self.current_index -= 1;
        }
        Some(&self.matches[self.current_index])
    }

    /// Get all matches for a specific message index.
    pub fn matches_for_message(&self, idx: usize) -> Vec<&SearchMatch> {
        self.matches
            .iter()
            .filter(|m| m.message_index == idx)
            .collect()
    }

    /// Total number of matches found.
    pub fn total_matches(&self) -> usize {
        self.matches.len()
    }

    /// The currently focused match.
    pub fn current(&self) -> Option<&SearchMatch> {
        self.matches.get(self.current_index)
    }

    /// Clear all search state.
    pub fn clear(&mut self) {
        self.matches.clear();
        self.current_index = 0;
    }
}

impl Default for DialogSearchEngine {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn msgs(data: &[(&str, &str)]) -> Vec<(String, String)> {
        data.iter()
            .map(|(t, r)| (t.to_string(), r.to_string()))
            .collect()
    }

    #[test]
    fn empty_query_returns_zero() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[("Hello world", "")]);
        assert_eq!(engine.search(&messages, ""), 0);
        assert!(engine.current().is_none());
    }

    #[test]
    fn empty_messages_returns_zero() {
        let mut engine = DialogSearchEngine::new();
        let messages: Vec<(String, String)> = vec![];
        assert_eq!(engine.search(&messages, "hello"), 0);
    }

    #[test]
    fn single_match_in_text() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[("Hello World", "")]);
        assert_eq!(engine.search(&messages, "world"), 1);
        let m = engine.current().unwrap();
        assert_eq!(m.message_index, 0);
        assert_eq!(m.start, 6);
        assert_eq!(m.length, 5);
        assert_eq!(m.field, SearchField::Text);
    }

    #[test]
    fn multiple_matches_across_fields() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[
            ("The cat sat on the mat", "The cat was thinking"),
            ("Dogs are great", ""),
        ]);
        // msg0 text: "the" at 0, "the" at 15 → 2 matches
        // msg0 reasoning: "the" at 0 → 1 match
        // msg1 text: none
        assert_eq!(engine.search(&messages, "the"), 3);
        assert_eq!(engine.matches_for_message(0).len(), 3);
        assert_eq!(engine.matches_for_message(1).len(), 0);
        // Check that we found reasoning matches
        let reasoning_matches: Vec<_> = engine
            .matches_for_message(0)
            .into_iter()
            .filter(|m| m.field == SearchField::Reasoning)
            .collect();
        assert_eq!(reasoning_matches.len(), 1);
    }

    #[test]
    fn case_insensitive_matching() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[("OpenAI GPT Model", "")]);
        assert_eq!(engine.search(&messages, "openai"), 1);
        assert_eq!(engine.search(&messages, "OPENAI"), 1);
        assert_eq!(engine.search(&messages, "OpenAI"), 1);
    }

    #[test]
    fn circular_navigation_next() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[("aaa", "bbb")]);
        engine.search(&messages, "a");
        assert_eq!(engine.total_matches(), 3);

        // current starts at index 0
        assert_eq!(engine.current().unwrap().start, 0);

        // next → index 1
        let m = engine.navigate_next().unwrap();
        assert_eq!(m.start, 1);

        // next → index 2
        let m = engine.navigate_next().unwrap();
        assert_eq!(m.start, 2);

        // next → wraps to index 0
        let m = engine.navigate_next().unwrap();
        assert_eq!(m.start, 0);
    }

    #[test]
    fn circular_navigation_previous() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[("ab ab", "")]);
        engine.search(&messages, "ab");
        assert_eq!(engine.total_matches(), 2);

        // current at 0, previous wraps to last
        let m = engine.navigate_previous().unwrap();
        assert_eq!(m.start, 3);

        // previous again → back to 0
        let m = engine.navigate_previous().unwrap();
        assert_eq!(m.start, 0);
    }

    #[test]
    fn clear_resets_state() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[("test data", "")]);
        engine.search(&messages, "test");
        assert_eq!(engine.total_matches(), 1);

        engine.clear();
        assert_eq!(engine.total_matches(), 0);
        assert!(engine.current().is_none());
    }

    #[test]
    fn navigate_on_empty_returns_none() {
        let mut engine = DialogSearchEngine::new();
        assert!(engine.navigate_next().is_none());
        assert!(engine.navigate_previous().is_none());
    }

    #[test]
    fn no_match_query() {
        let mut engine = DialogSearchEngine::new();
        let messages = msgs(&[("Hello World", "Thinking deeply")]);
        assert_eq!(engine.search(&messages, "xyz"), 0);
        assert!(engine.current().is_none());
    }
}
