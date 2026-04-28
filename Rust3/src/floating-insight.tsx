import React from "react";
import ReactDOM from "react-dom/client";
import { FloatingInsightApp } from "./views/FloatingInsightWindow";
import "./index.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <FloatingInsightApp />
  </React.StrictMode>
);
