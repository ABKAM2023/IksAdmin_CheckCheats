**[IksAdmin] Check Cheats** - плагин позволяет администратору вызвать подозрительного игрока на проверку при обнаружении сомнительной активности.

## Возможности:
- Настройка причины бана
- Настройка длительности бана 
- Настройка длительности на скидывания Discord
- Возможность установить любой флаг доступа для проверки
- Воспроизведение звука при начале проверки
- Включение/выключение автобана при выходе после предоставления контакта
- Включение/выключение автоперемещения в наблюдатели на время проверки
- Включение/выключение блокировки смены команды во время проверки
- Логи в Discord
- Возможность изменить любое сообщение плагина
- Администратору также отображается информация о проверке

## Фотокарточки:
От лица подозреваемого:

![image](https://github.com/user-attachments/assets/3206eb55-d98e-43a4-8534-2ed542ff226a)

От лица администратора:

![image](https://github.com/user-attachments/assets/047ef07d-2808-4fbf-b5c7-0c2038369859)

## Конфигурационный файл
```json
{
  "ban_reason": "Отказ от проверки",
  "ban_time": 0,
  "check_duration": 120,
  "flag": "c",
  "check_sound_path": "sounds/buttons/button8.vsnd_c",

  "ban_on_disconnect_after_contact": true,
  "move_to_spectators_on_check": true,
  "block_team_change_during_check": true,

  "enable_discord_logging": false,
  "webhook_mode": 1,
  "discord_webhook_url": "",
  "discord_color_check_started": "FFA500",
  "discord_color_contact_provided": "00FF00",
  "discord_color_check_completed": "00FF00",
  "discord_footer_icon_url": "https://i.imgur.com/2NbqQu7.png",
  
  "enable_database_logging": false,  
  "server_id": 1,
  "database_host": "localhost",
  "database_user": "root",
  "database_name": "checkcheats_db",
  "database_password": "",
  "database_port": 3306,
  "table_name": "checkcheats_stats",
  
  "ConfigVersion": 1
}
```

## Переводы
```json
{
    "chat_prefix": "{White}[ {Red}ADMIN {White}] ",

    "check": "Вы успешно вызвали на проверку {0}",
    "uncheck": "Вы успешно сняли проверку с {0}",

    "select_player_for_check": "Вызывать на проверку",
    "select_player_to_uncheck": "Снять проверку",
    "status_check": "(уже вызван)",

    "admin_message_format": "Игрок {0} предоставил свой Дискорд: {1}",

    "contact_success_message": "Вы успешно предоставили Discord. Ожидайте, администратор скоро отправит вам запрос в Discord.",
    "check_start_message": "Вы были перемещены в наблюдатели для проверки",
    "check_team_lock_message": "Вы не можете сменить команду, пока находитесь на проверке",

    "error_no_active_check_for_admin": "У {0} нету активной проверки.",
    "error_message": "Пожалуйста, укажите ваш дискорд. Используйте: {Green}!contact ваш_дискорд",    
    "error_no_active_check": "У вас нет активной проверки.",

    "message_closed": "Ваши активные проверки успешно закрыты.",

    "html_countdown_message_format": "<font color='white' class='fontSize-m+'>Вы были вызваны на проверку. Оставшееся время: <font color='red'>{0} сек.</font> Отправьте свой <font color='#5865F2'>Discord</font> через команду: <font color='yellow'>!contact ваш_дискорд</font></font>",
    "html_admin_check_info_message": "<font color='white' class='fontSize-m'>Проверка игрока:</font><br><font color='red' class='fontSize-m'>{0}</font><br><font color='white' class='fontSize-m'>Оставшееся время: <font color='yellow' class='fontSize-l'>{1} сек.</font></font><br><font color='gray' class='fontSize-s'>Для скрытия сообщения напишите !close</font>",
    "html_success_message": "<font color='green' class='fontSize-m+'>Вы успешно прошли проверку.</font>",

    "db_check_result_player_left_and_banned": "Игрок вышел и был забанен",
    "db_check_result_completed": "Проверка пройдена",

    "discord_check_started_title": "🔍 Проверка начата",
    "discord_contact_provided_title": "📱 Дискорд предоставлен",
    "discord_check_completed_title": "✅ Проверка завершена",
    "discord_consolidated_check_title": "📋 Результат проверки",    

    "discord_check_started_description": "Игрок был вызван на проверку.",
    "discord_check_completed_description": "Проверка игрока успешно завершена.",
    "discord_contact_provided_description": "Игрок предоставил свой Discord контакт.",
    "discord_consolidated_check_description": "Информация о проверке игрока администратором.",
    
    "discord_check_started_player_field": "Игрок",
    "discord_check_started_admin_field": "Администратор",
    "discord_contact_field": "Дискорд",
    "discord_check_result_field": "Результат проверки",
    "discord_contact_not_provided": "❌ Не предоставлен",

    "check_result_player_left_before_completion": "Игрок вышел до завершения проверки",
    "check_result_completed": "Проверка завершена",

    "discord_system_footer_text": "Система проверки",
    "discord_system_footer_icon_url": "https://i.imgur.com/2NbqQu7.png"
}

```
