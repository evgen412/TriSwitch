# Сторонние данные

TriSwitch использует неизменённые словари из официального репозитория [LibreOffice/dictionaries](https://github.com/LibreOffice/dictionaries), ревизия **32b006a2c22a4ac7e8ed3f03346f7b3d85a970a4**. Скачаны файлы `.dic`, `.aff` и сопроводительные документы; исполняемый код LibreOffice не включён. Программа читает словари локально.

| Словарь | Папка | Лицензии и сведения об авторах |
|---|---|---|
| English (US), SCOWL, 2020.12.07 | `dictionaries/en` | Полные условия разных источников SCOWL и copyright сохранены в `README_en_US.txt`. Дополнительно сохранён исходный `license.txt` из каталога en. |
| Русский, Alexander I. Lebedev | `dictionaries/ru_RU` | Условия распространения и copyright сохранены в `README_ru_RU.txt`. Файлы не изменены. |
| Українська, dict_uk | `dictionaries/uk_UA` | `README_uk_UA.txt`: Mozilla Public License 1.1. Полный официальный текст в `LICENSE-MPL-1.1.txt`. Исходные `.dic` и `.aff` включены без изменений. |

Первоисточник украинского словаря: [brown-uk/dict_uk](https://github.com/brown-uk/dict_uk). Официальный текст MPL 1.1: [mozilla.org](https://www.mozilla.org/media/MPL/1.1/index.txt).

При передаче программы сохраняйте папку `dictionaries` со всеми сопроводительными файлами. Авторство словарей принадлежит указанным в них правообладателям.

Справочники, использованные при разработке Windows-интеграции: [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [ToUnicodeEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-tounicodeex).
