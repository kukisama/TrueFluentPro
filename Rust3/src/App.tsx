import { useTranslation } from "react-i18next";

function App() {
  const { t } = useTranslation();
  return <div>{t("app.name")}</div>;
}

export default App;
